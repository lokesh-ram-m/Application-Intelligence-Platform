namespace Aip.Core.Abstractions;

/// <summary>
/// The Core PORT for an AI provider. Providers (GitHub Models, Azure AI Foundry, OpenAI) are
/// swappable adapters in Infrastructure. AI consumes grounded, graph-derived input and never
/// becomes the source of truth (Platform Architecture, Session 4 §5).
/// </summary>
public interface IAiProvider
{
    /// <summary>Render a completion from a system + user prompt. The user prompt is always a grounded view model.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>
/// Optional capability: a provider that can identify which service and model it's actually configured
/// against — for observability only (e.g. Run History), never consulted for AI behavior. Implemented by
/// OpenAiCompatibleProvider since one class now serves both GitHub Models and Azure AI Foundry, and its
/// type name alone can no longer distinguish them.
/// </summary>
public interface IAiProviderDescriptor
{
    string ProviderName { get; }
    string Model { get; }
}

/// <summary>Raised by an <see cref="IAiProvider"/> when a completion ultimately fails — either a
/// non-retryable error or a retryable one whose retries were exhausted. Part of the port's contract (not
/// an Infrastructure-only concern), so callers above Infrastructure — e.g. Aip.Projections, classifying a
/// fallback-to-deterministic — can distinguish failure kinds without depending on any concrete provider.
/// <see cref="StatusCode"/> is the originating HTTP status, when the failure came from one (e.g. 429).</summary>
public sealed class AiProviderException : Exception
{
    public int? StatusCode { get; }

    public AiProviderException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
}
