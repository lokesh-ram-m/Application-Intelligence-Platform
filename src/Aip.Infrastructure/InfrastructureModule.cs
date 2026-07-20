using System.Collections.Concurrent;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Documents;
using Aip.Abstractions.History;
using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Observability;
using Aip.Abstractions.Projections;
using Aip.Core.Abstractions;
using Aip.Core.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aip.Infrastructure;

/// <summary>
/// Readiness signal for <c>serve</c> mode's <c>/health</c> endpoint — verifies Run History's SQL Server
/// database is actually reachable, since that's the one dependency the platform can't function without
/// (every run needs it before it can even check <c>skipIfUnchanged</c>). Deliberately not registered as a
/// liveness check — a database blip shouldn't kill the process, but it should stop it being routed to.
/// </summary>
internal sealed class SqlHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<RunHistoryDbContext> _factory;

    public SqlHealthCheck(IDbContextFactory<RunHistoryDbContext> factory) => _factory = factory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);

            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("SQL Server did not respond.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server unreachable.", ex);
        }
    }
}

internal sealed class InMemoryExecutionStore : IExecutionStore
{
    private readonly ConcurrentDictionary<Guid, ExecutionResult> _results = new();

    public Task SaveAsync(ExecutionResult result, CancellationToken ct = default)
    {
        _results[result.ExecutionId.Value] = result;

        return Task.CompletedTask;
    }

    public Task<ExecutionResult?> GetAsync(Guid executionId, CancellationToken ct = default)
        => Task.FromResult(_results.TryGetValue(executionId, out ExecutionResult? r) ? r : null);
}

internal sealed class NoOpAiProvider : IAiProvider
{
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        => throw new NotSupportedException("No AI provider configured (set AIP_GITHUB_TOKEN). Deterministic projection is used.");
}

public static class InfrastructureModule
{
    public static IServiceCollection AddAipInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Guarantees ILogger<T> always resolves for every adapter registered below that asks for one
        // (GitRepositorySource, RoslynLanguageEngine, EfAiFallbackStore, …), even when the real Serilog
        // pipeline (AddAipLogging, wired separately by the real entry points) hasn't been — e.g. a test
        // composing this module directly, or any future caller that doesn't go through the full
        // PlatformComposition. Calling AddLogging() more than once (Serilog's own AddAipLogging included)
        // is safe; the underlying registrations use TryAdd semantics.
        services.AddLogging();

        // Knowledge Store — same SQL Server database as Run History (see EfKnowledgeRepository); one
        // instance behind both the write port and the read store. Registered here (not down by the other
        // SQL Server registrations below) only because DI resolution order doesn't matter for singletons —
        // AddDbContextFactory<RunHistoryDbContext> is what EfKnowledgeRepository actually needs at
        // resolve time, and that's registered further down in this same method.
        services.AddSingleton<EfKnowledgeRepository>();
        services.AddSingleton<IKnowledgeRepository>(sp => sp.GetRequiredService<EfKnowledgeRepository>());
        services.AddSingleton<IKnowledgeStore>(sp => sp.GetRequiredService<EfKnowledgeRepository>());

        services.AddSingleton<IExecutionStore, InMemoryExecutionStore>();

        // Git credentials, keyed by host — e.g. Git:Credentials:dev.azure.com — so a private repo's PAT
        // covers every repo on that host without repeating it per apps.yml entry. Sourced from
        // appsettings.json/appsettings.Development.json; override any host's token with an env var of
        // the same shape (e.g. Git__Credentials__dev.azure.com), same as every other setting.
        var gitTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection section in configuration.GetSection("Git:Credentials").GetChildren())
            if (!string.IsNullOrWhiteSpace(section.Value))
                gitTokens[section.Key] = section.Value;
        services.AddSingleton(new GitCredentials(gitTokens));

        services.AddSingleton<IRepositorySource, GitRepositorySource>();
        services.AddSingleton<IArtifactDiscovery, RepositoryScanner>();

        // Document store: the durable, servable location the Document Viewer reads from live, on every
        // request — no local site is generated by the Creator anymore. Filesystem is the default; the
        // Host swaps in Azure Blob (Aip.Infrastructure.AzureBlob) when configured for standalone/production use.
        services.AddSingleton<IDocumentStore, FileSystemDocumentStore>();

        // AI provider. Both real providers speak the same OpenAI-compatible chat-completions wire format,
        // so one class (OpenAiCompatibleProvider) serves both — only the token/endpoint/model differ.
        // Which one is active is controlled by Ai:Provider (or AIP_AI_PROVIDER): "AzureFoundry" |
        // "GitHubModels" | "Auto" (default). This is a deliberate switch, not a credential swap — both
        // sets of keys can sit in config at once, and flipping providers is a one-line config change
        // instead of clearing/re-pasting a key every time you want to go back. "Auto" prefers Azure
        // Foundry when its API key is present, else falls back to GitHub Models, else a no-op
        // (deterministic docs). Sourced from appsettings.json / appsettings.Development.json, with an env
        // var always winning — that precedence is how production supplies the real secret (pipeline/App
        // Service secret), with no code path difference from local dev.
        string? foundryKey = Environment.GetEnvironmentVariable("AIP_AZURE_FOUNDRY_API_KEY") ?? configuration["Ai:AzureFoundry:ApiKey"];
        string? githubToken = Environment.GetEnvironmentVariable("AIP_GITHUB_TOKEN") ?? configuration["Ai:GitHubToken"];
        string providerPreference = Environment.GetEnvironmentVariable("AIP_AI_PROVIDER") ?? configuration["Ai:Provider"] ?? "Auto";
        bool forcedGitHub = providerPreference.Equals("GitHubModels", StringComparison.OrdinalIgnoreCase);

        // Retry/timeout policy — shared by whichever provider ends up active below. Ai:MaxRetries /
        // Ai:TimeoutSeconds / Ai:RetryDelayMs (env vars win, matching every other setting); an absent or
        // unparsable value falls back to AiProviderOptions' own defaults rather than failing startup, since
        // these are tuning knobs, not credentials.
        int maxRetries = ReadInt("AIP_AI_MAX_RETRIES", "Ai:MaxRetries", new AiProviderOptions().MaxRetries);
        int timeoutSeconds = ReadInt("AIP_AI_TIMEOUT_SECONDS", "Ai:TimeoutSeconds", new AiProviderOptions().TimeoutSeconds);
        int retryDelayMs = ReadInt("AIP_AI_RETRY_DELAY_MS", "Ai:RetryDelayMs", new AiProviderOptions().RetryDelayMs);

        // TPM is NOT shared the way retry/timeout are — a primary model and its fallback are frequently
        // very different deployments with very different real quotas (e.g. a small, rarely-used fallback
        // deployment sized well below the primary's), so throttling both to the same number would either
        // cripple the primary or leave an undersized fallback unprotected. Each tier reads its own key,
        // falling back to the shared Ai:MaxTokensPerMinute only when it has no more specific override; the
        // shared default itself is 0 (disabled) — this app can't know a deployment's real quota unless told.
        int sharedMaxTpm = ReadInt("AIP_AI_MAX_TPM", "Ai:MaxTokensPerMinute", new AiProviderOptions().MaxTokensPerMinute);
        int foundryMaxTpm = ReadInt("AIP_AZURE_FOUNDRY_MAX_TPM", "Ai:AzureFoundry:MaxTokensPerMinute", sharedMaxTpm);
        int foundryFallbackMaxTpm = ReadInt("AIP_AZURE_FOUNDRY_FALLBACK_MAX_TPM", "Ai:AzureFoundry:FallbackMaxTokensPerMinute", sharedMaxTpm);
        int githubMaxTpm = sharedMaxTpm;
        int githubFallbackMaxTpm = ReadInt("AIP_AI_FALLBACK_MAX_TPM", "Ai:FallbackMaxTokensPerMinute", sharedMaxTpm);

        int ReadInt(string envVar, string configKey, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(envVar) ?? configuration[configKey];

            return int.TryParse(raw, out int value) ? value : fallback;
        }

        string foundryModel = Environment.GetEnvironmentVariable("AIP_AZURE_FOUNDRY_DEPLOYMENT") ?? configuration["Ai:AzureFoundry:Deployment"] ?? "";
        string? foundryFallbackModel = Environment.GetEnvironmentVariable("AIP_AZURE_FOUNDRY_FALLBACK_DEPLOYMENT") ?? configuration["Ai:AzureFoundry:FallbackDeployment"];
        string foundryEndpoint = Environment.GetEnvironmentVariable("AIP_AZURE_FOUNDRY_ENDPOINT") ?? configuration["Ai:AzureFoundry:Endpoint"] ?? "";

        string githubModel = Environment.GetEnvironmentVariable("AIP_AI_MODEL") ?? configuration["Ai:Model"] ?? "openai/gpt-4o-mini";
        string? githubFallbackModel = Environment.GetEnvironmentVariable("AIP_AI_FALLBACK_MODEL") ?? configuration["Ai:FallbackModel"];
        string githubEndpoint = Environment.GetEnvironmentVariable("AIP_AI_ENDPOINT") ?? configuration["Ai:Endpoint"] ?? "https://models.github.ai/inference";

        // Primary/secondary provider follows the existing Ai:Provider preference (Auto prefers Foundry
        // unless forced to GitHubModels). Each provider is itself a chain: TPM-throttled primary model →
        // TPM-throttled fallback model (only when Ai:*FallbackDeployment/Ai:FallbackModel is configured) —
        // reusing FailoverAiProvider for model-level failover exactly as for provider-level failover below,
        // since "try A, then B" is the same problem either way. A page only falls back to deterministic
        // once every configured tier has failed (see DocumentationProjection.RecordFallbackAsync).
        services.AddSingleton<IAiProvider>(sp =>
        {
            IAiProvider? foundry = BuildProviderChain(sp, foundryKey, foundryModel, foundryFallbackModel, foundryEndpoint,
                "AzureFoundry", timeoutSeconds, maxRetries, retryDelayMs, foundryMaxTpm, foundryFallbackMaxTpm);
            IAiProvider? github = BuildProviderChain(sp, githubToken, githubModel, githubFallbackModel, githubEndpoint,
                "GitHubModels", timeoutSeconds, maxRetries, retryDelayMs, githubMaxTpm, githubFallbackMaxTpm);

            (IAiProvider? primary, string primaryName, IAiProvider? secondary, string secondaryName) =
                forcedGitHub || foundry is null
                    ? (github, "GitHubModels", foundry, "AzureFoundry")
                    : (foundry, "AzureFoundry", github, "GitHubModels");

            if (primary is not null && secondary is not null)
                return new FailoverAiProvider(primary, primaryName, secondary, secondaryName, sp.GetRequiredService<ILogger<FailoverAiProvider>>());
            if (primary is not null)
                return primary;

            return new NoOpAiProvider();
        });

        // Run History store: durable record of every pipeline run (application, trigger, commit(s)
        // analyzed, AI cost, pages generated) — separate from the Knowledge Store, which holds the graph
        // itself, AND separate from the Logs database (see LoggingModule.ResolveLoggingConnectionString) —
        // Run History and Logs are deliberately on their own connection strings so they can be scaled,
        // retained, and moved to Azure SQL independently of each other. SQL Server / Azure SQL only; no
        // SQLite fallback, so a missing connection string fails loudly at startup rather than silently
        // falling back to a different store shape.
        string sqlConnection = ResolveSqlConnectionString(configuration);
        services.AddDbContextFactory<RunHistoryDbContext>(options => options.UseSqlServer(sqlConnection));
        services.AddSingleton<IRunHistoryStore, EfRunHistoryStore>();

        // Real /health check (see Aip.Host/Serve.cs) — verifies the one dependency the platform genuinely
        // can't function without. Registered here regardless of entry point; only a web-hosting caller
        // (serve mode) ever actually maps it to a route, so this is inert for the plain CLI path.
        services.AddHealthChecks().AddCheck<SqlHealthCheck>("sql", tags: new[] { "ready" });

        // AI fallback events — same database/context as Run History (see AiFallbackEventEntity); a
        // separate table, not a separate connection string, since it's structured run-adjacent data rather
        // than freeform operational logging (that's what the Logs database/Serilog sink is for).
        services.AddSingleton<IAiFallbackStore, EfAiFallbackStore>();

        // Version-to-version documentation change records — same database/context again.
        services.AddSingleton<IVersionChangeStore, EfVersionChangeStore>();

        return services;
    }

    /// <summary>
    /// Read access to version-change records — a minimal slice of <see cref="AddAipInfrastructure"/>'s full
    /// registration (git sourcing, AI providers, document store, …) for a caller that only ever reads, like
    /// the Viewer, which has no reason to pull in everything a full Creator composition needs.
    /// </summary>
    public static IServiceCollection AddAipVersionChanges(this IServiceCollection services, IConfiguration configuration)
    {
        string sqlConnection = ResolveSqlConnectionString(configuration);
        services.AddDbContextFactory<RunHistoryDbContext>(options => options.UseSqlServer(sqlConnection));
        services.AddSingleton<IVersionChangeStore, EfVersionChangeStore>();

        return services;
    }

    /// <summary>
    /// Applies pending Run History migrations, creating the database on first run. Call once at startup
    /// (see <c>Aip.Host/Program.cs</c>) — cheap and idempotent, so it is safe to call on every process start.
    /// </summary>
    public static async Task MigrateRunHistoryAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await services.GetRequiredService<IDbContextFactory<RunHistoryDbContext>>().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// The one SQL connection string every SQL-backed concern shares (Run History, Logs) — resolved once
    /// here so Run History wiring and Serilog wiring (<see cref="LoggingModule"/>) can never silently
    /// disagree about which database they're pointed at. AIP_SQL_CONNECTION_STRING (env var) always wins
    /// over History:ConnectionString (appsettings); missing entirely is a startup failure, not a silent
    /// fallback to a different store shape.
    /// </summary>
    public static string ResolveSqlConnectionString(IConfiguration configuration)
    {
        string? sqlConnection = Environment.GetEnvironmentVariable("AIP_SQL_CONNECTION_STRING") ?? configuration["History:ConnectionString"];
        if (string.IsNullOrWhiteSpace(sqlConnection))
            throw new InvalidOperationException(
                "No SQL connection string configured. Set History:ConnectionString " +
                "(appsettings.Development.json) or AIP_SQL_CONNECTION_STRING (env var) to a SQL Server / " +
                "Azure SQL connection string. See README.md for the local Docker SQL Server setup.");

        return sqlConnection;
    }

    /// <summary>
    /// Builds one provider's full chain: TPM-throttled primary model, and — only when a fallback model is
    /// configured — TPM-throttled fallback model behind the same FailoverAiProvider used for cross-provider
    /// failover (see AddAipInfrastructure). <paramref name="maxTokensPerMinute"/> and
    /// <paramref name="fallbackMaxTokensPerMinute"/> are independent — a fallback deployment is frequently
    /// sized very differently from its primary (e.g. a small, rarely-used deployment kept deliberately
    /// cheap), so they must never share one throttle value. Returns null when <paramref name="key"/> is
    /// absent, so the caller can tell "not configured" apart from "configured but currently failing."
    /// </summary>
    private static IAiProvider? BuildProviderChain(IServiceProvider sp, string? key, string model, string? fallbackModel,
        string endpoint, string providerName, int timeoutSeconds, int maxRetries, int retryDelayMs,
        int maxTokensPerMinute, int fallbackMaxTokensPerMinute)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var options = new AiProviderOptions(Model: model, Endpoint: endpoint,
            TimeoutSeconds: timeoutSeconds, MaxRetries: maxRetries, RetryDelayMs: retryDelayMs);
        IAiProvider primary = new TpmThrottledAiProvider(new OpenAiCompatibleProvider(key, options, providerName), maxTokensPerMinute);

        if (string.IsNullOrWhiteSpace(fallbackModel)) return primary;

        IAiProvider fallback = new TpmThrottledAiProvider(
            new OpenAiCompatibleProvider(key, options with { Model = fallbackModel }, providerName), fallbackMaxTokensPerMinute);

        return new FailoverAiProvider(primary, $"{providerName}:{model}", fallback, $"{providerName}:{fallbackModel}",
            sp.GetRequiredService<ILogger<FailoverAiProvider>>());
    }
}
