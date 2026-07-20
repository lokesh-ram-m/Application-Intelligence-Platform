using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Aip.Abstractions.Ai;
using Aip.Core.Abstractions;

using Microsoft.Extensions.Logging;

namespace Aip.Infrastructure;

// AiProviderException lives in Aip.Core.Abstractions (see IAiProvider.cs) — it's part of the port's
// contract, not an Infrastructure-only concern, since callers above Infrastructure (Aip.Projections)
// need to classify AI failures without depending on any concrete provider.

/// <summary>Provider configuration (model, endpoint, timeout, retry policy). MaxRetries/TimeoutSeconds/
/// RetryDelayMs/MaxTokensPerMinute are sourced from Ai:MaxRetries / Ai:TimeoutSeconds / Ai:RetryDelayMs /
/// Ai:MaxTokensPerMinute in appsettings — see InfrastructureModule — these defaults only apply when a key
/// is absent from configuration entirely. MaxTokensPerMinute = 0 means "no TPM throttle" (the default,
/// since the real per-deployment quota isn't something this app can know without being told).</summary>
public sealed record AiProviderOptions(
    string Model = "openai/gpt-4o-mini",
    string Endpoint = "https://models.inference.ai.azure.com",
    int TimeoutSeconds = 100,
    int MaxRetries = 3,
    int RetryDelayMs = 500,
    int MaxTokensPerMinute = 0);

/// <summary>
/// Provider for any OpenAI-compatible chat-completions endpoint (GitHub Models, Azure AI Foundry's
/// unified <c>/openai/v1</c> API, or anything else speaking the same wire format). Handles timeouts,
/// cancellation, retries with exponential backoff, rate-limit (429) Retry-After honouring, structured
/// error reporting, configurable model/endpoint, and real token usage. A swappable adapter behind the
/// Core <c>IAiProvider</c> port; it receives grounded prompts from the AI Platform — never raw source.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider, IAiUsageReporter, IAiProviderDescriptor
{
    // Server statuses worth a bounded retry — up to AiProviderOptions.MaxRetries, each capped at
    // MaxRetryDelay so a throttled/degraded AI never stalls the whole documentation run. 429 sits
    // alongside the transient 5xx statuses rather than failing fast, since GitHub Models/Azure Foundry
    // both send a short Retry-After for ordinary rate limiting that a bounded wait can ride out.
    private static readonly HttpStatusCode[] Retryable =
        { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout };

    // Never wait out a long Retry-After — a throttled AI must not stall the whole documentation run.
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly AiProviderOptions _options;
    private readonly string _providerName;
    private volatile AiUsage? _lastUsage;

    private readonly string _completionsUrl;

    /// <summary><paramref name="providerName"/> identifies which real service this instance talks to
    /// (e.g. "AzureFoundry", "GitHubModels") — since this one class serves both, its type name alone
    /// can't tell them apart in observability (Run History). See <see cref="IAiProviderDescriptor"/>.</summary>
    public OpenAiCompatibleProvider(string token, AiProviderOptions? options = null, string providerName = "OpenAiCompatible")
    {
        _options = options ?? new AiProviderOptions();
        _providerName = providerName;
        // Build the absolute completions URL so path segments in the endpoint (e.g. ".../inference") are
        // preserved — a leading-slash relative path would otherwise reset them.
        _completionsUrl = _options.Endpoint.TrimEnd('/') + "/chat/completions";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public string ProviderName => _providerName;
    public string Model => _options.Model;

    public AiUsage? LastUsage => _lastUsage;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync(_completionsUrl, payload, ct);
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return await ParseAsync(response, ct);

                if (Retryable.Contains(response.StatusCode) && attempt < _options.MaxRetries)
                {
                    TimeSpan delay = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                    if (delay > MaxRetryDelay) delay = MaxRetryDelay;   // cap the wait — never stall the run
                    await Task.Delay(delay, ct);
                    continue;
                }

                string body = await response.Content.ReadAsStringAsync(ct);
                string reason = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? $"AI provider rate limit (429) still in effect after {attempt} retr{(attempt == 1 ? "y" : "ies")}"
                    : $"AI provider returned {(int)response.StatusCode} {response.StatusCode}";
                throw new AiProviderException($"{reason}: {Truncate(body)}", (int)response.StatusCode);
            }
        }
    }

    private async Task<string> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        JsonElement doc = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (doc.TryGetProperty("usage", out JsonElement usage) &&
            usage.TryGetProperty("prompt_tokens", out JsonElement pt) &&
            usage.TryGetProperty("completion_tokens", out JsonElement ctk))
            _lastUsage = new AiUsage(pt.GetInt32(), ctk.GetInt32());

        return doc.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(_options.RetryDelayMs * Math.Pow(2, attempt));

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}

/// <summary>
/// Tries a primary AI provider first; if it fails for any reason (after that provider's own internal
/// retries are exhausted — see OpenAiCompatibleProvider), retries the same completion against a secondary
/// provider before giving up. A page only falls back to deterministic rendering (DocumentationProjection)
/// once BOTH providers have failed, not just one — so one provider being down, misconfigured, or
/// persistently rate-limited doesn't by itself take narrative pages back to deterministic when a second,
/// independently-configured provider is available. Only constructed when both are actually configured
/// (see InfrastructureModule); a single-provider setup uses that provider directly, unwrapped.
/// </summary>
public sealed class FailoverAiProvider : IAiProvider, IAiProviderDescriptor
{
    private readonly IAiProvider _primary;
    private readonly string _primaryName;
    private readonly IAiProvider _secondary;
    private readonly string _secondaryName;
    private readonly ILogger<FailoverAiProvider> _log;
    private volatile string _lastProviderName;

    public FailoverAiProvider(IAiProvider primary, string primaryName, IAiProvider secondary, string secondaryName, ILogger<FailoverAiProvider> log)
    {
        _primary = primary;
        _primaryName = primaryName;
        _secondary = secondary;
        _secondaryName = secondaryName;
        _log = log;
        _lastProviderName = primaryName;
    }

    // Observability only (Run History) — reflects whichever provider most recently actually served a
    // completion, same one-sample-per-read precision Run History already has for a single-provider setup.
    public string ProviderName => _lastProviderName;
    public string Model => (_lastProviderName == _primaryName ? _primary : _secondary) is IAiProviderDescriptor d ? d.Model : "";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        try
        {
            string result = await _primary.CompleteAsync(systemPrompt, userPrompt, ct);
            _lastProviderName = _primaryName;

            return result;
        }
        catch (Exception primaryEx) when (primaryEx is not OperationCanceledException)
        {
            _log.LogWarning(primaryEx, "AI provider {Primary} failed — failing over to {Secondary}", _primaryName, _secondaryName);
            string result = await _secondary.CompleteAsync(systemPrompt, userPrompt, ct);
            _lastProviderName = _secondaryName;

            return result;
        }
    }
}

/// <summary>
/// Tracks token usage in a trailing 60-second window and blocks a caller until there's headroom for an
/// upcoming call — a sliding-window token-bucket rate limiter. Admission uses an estimate (the real count
/// is only known after a completion returns — see <see cref="TpmThrottledAiProvider"/>), so this exists to
/// keep the app under a provider's actual TPM quota pre-emptively, rather than reacting to 429s after the
/// fact. Disabled entirely (no waiting, no bookkeeping) when <paramref name="maxTokensPerMinute"/> is 0.
/// </summary>
internal sealed class TokenRateLimiter
{
    private readonly int _maxTokensPerMinute;
    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset At, int Tokens)> _window = new();

    public TokenRateLimiter(int maxTokensPerMinute) => _maxTokensPerMinute = maxTokensPerMinute;

    public bool Enabled => _maxTokensPerMinute > 0;

    public async Task WaitForCapacityAsync(int estimatedTokens, CancellationToken ct)
    {
        if (!Enabled) return;

        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                Prune();
                int used = _window.Sum(e => e.Tokens);
                if (used + estimatedTokens <= _maxTokensPerMinute) return;

                DateTimeOffset oldest = _window.Peek().At;   // window is non-empty here — used > 0 implies at least one entry
                TimeSpan untilOldestAges = oldest.AddMinutes(1) - DateTimeOffset.UtcNow;
                wait = untilOldestAges > TimeSpan.Zero ? untilOldestAges : TimeSpan.FromMilliseconds(200);
            }

            await Task.Delay(wait, ct);
        }
    }

    public void Record(int tokens)
    {
        if (!Enabled || tokens <= 0) return;

        lock (_gate)
        {
            _window.Enqueue((DateTimeOffset.UtcNow, tokens));
            Prune();
        }
    }

    private void Prune()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        while (_window.Count > 0 && _window.Peek().At < cutoff) _window.Dequeue();
    }
}

/// <summary>
/// Wraps another <see cref="IAiProvider"/> with a TPM (tokens-per-minute) throttle — waits for window
/// capacity before every call, so a documentation run paces itself under a provider's real quota instead
/// of firing calls back-to-back until it gets a 429. Admission is a cheap ~4-chars/token estimate (the
/// real count is unknowable before the call); once a call completes, the actual reported usage (or the
/// estimate, on failure — a failed call may still have consumed real quota) corrects the window. Forwards
/// <see cref="IAiUsageReporter"/>/<see cref="IAiProviderDescriptor"/> transparently so it's invisible to
/// FailoverAiProvider and Run History wherever it sits in the provider chain.
/// </summary>
public sealed class TpmThrottledAiProvider : IAiProvider, IAiUsageReporter, IAiProviderDescriptor
{
    // A single documentation section/page response is typically a few hundred words — this is a rough
    // ballpark for the completion side of the estimate, refined by real usage after each call.
    private const int EstimatedCompletionTokens = 800;

    private readonly IAiProvider _inner;
    private readonly TokenRateLimiter _limiter;

    public TpmThrottledAiProvider(IAiProvider inner, int maxTokensPerMinute)
    {
        _inner = inner;
        _limiter = new TokenRateLimiter(maxTokensPerMinute);
    }

    public string ProviderName => (_inner as IAiProviderDescriptor)?.ProviderName ?? "Unknown";
    public string Model => (_inner as IAiProviderDescriptor)?.Model ?? "";
    public AiUsage? LastUsage => (_inner as IAiUsageReporter)?.LastUsage;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        int estimate = EstimateTokens(systemPrompt) + EstimateTokens(userPrompt) + EstimatedCompletionTokens;
        await _limiter.WaitForCapacityAsync(estimate, ct);

        try
        {
            string result = await _inner.CompleteAsync(systemPrompt, userPrompt, ct);
            int actual = (_inner as IAiUsageReporter)?.LastUsage?.Total ?? 0;
            _limiter.Record(actual > 0 ? actual : estimate);

            return result;
        }
        catch
        {
            _limiter.Record(estimate);
            throw;
        }
    }

    // Cheap, provider-agnostic heuristic (~4 chars/token for English text) — good enough for admission
    // control; real usage (recorded after each call) is what actually governs the window over time.
    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);
}
