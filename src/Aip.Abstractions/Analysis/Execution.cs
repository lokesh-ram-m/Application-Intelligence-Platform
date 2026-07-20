using Aip.Core.Domain;

namespace Aip.Abstractions.Analysis;

/// <summary>The normalized input to the one pipeline — an application to (re)analyze.</summary>
public sealed record ExecutionRequest(
    ApplicationId Application,
    ExecutionMode Mode);

/// <summary>
/// The durable, immutable record of an execution: the emitted (pre-validation) Discoveries plus
/// diagnostics and metrics. The Validation Pipeline is the only thing that turns Discoveries into
/// committed Knowledge — this result carries what was proposed, not what was accepted.
/// </summary>
public sealed record ExecutionResult(
    ExecutionId ExecutionId,
    ApplicationId Application,
    ExecutionOutcome Outcome,
    IReadOnlyList<Discovery> Discoveries,
    IReadOnlyList<Diagnostic> Diagnostics,
    ExecutionMetrics Metrics,
    SnapshotId? Snapshot = null);

/// <summary>The single analysis pipeline shared by all execution modes.</summary>
public interface IAnalysisPipeline
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct = default);
}
