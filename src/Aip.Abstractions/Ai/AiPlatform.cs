namespace Aip.Abstractions.Ai;

/// <summary>Token usage for one AI interaction — attributable per execution/plugin/projection.</summary>
public sealed record AiUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;
}

/// <summary>Meters and caps AI usage on both the write and read paths, feeding one cost ledger.</summary>
public interface ITokenAccountant
{
    void Record(string scope, AiUsage usage);
    AiUsage Total { get; }
    IReadOnlyDictionary<string, AiUsage> ByScope { get; }
}

/// <summary>Turns a grounded projection view model into the text the AI is allowed to see.</summary>
public interface IContextBuilder
{
    string Build(object viewModel);
}

/// <summary>Implemented by providers that can report real token usage from the last API response.</summary>
public interface IAiUsageReporter
{
    AiUsage? LastUsage { get; }
}

/// <summary>One recorded AI interaction (for history/audit/cost).</summary>
public sealed record AiExecution(string Template, string Provider, AiUsage Usage, DateTimeOffset At);

/// <summary>The AI execution history — audit trail of every AI interaction.</summary>
public interface IAiExecutionHistory
{
    IReadOnlyList<AiExecution> Records { get; }
}

/// <summary>
/// The single boundary all AI flows through. Enforces grounding: the model only ever sees graph-derived
/// view models, never raw repositories. Records tokens and history for every call.
/// </summary>
public interface IAiPlatform
{
    bool IsAvailable { get; }
    Task<string> RenderAsync(string templateName, IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
}
