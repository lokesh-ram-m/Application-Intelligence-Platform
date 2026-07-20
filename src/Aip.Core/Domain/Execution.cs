namespace Aip.Core.Domain;

/// <summary>
/// Execution trigger mode. There is a single trigger today — the standalone/scheduled batch run (see
/// Aip.Host's `serve`/`run --config` modes) — but this stays a distinct value rather than being inlined,
/// matching Run History's <c>TriggerType</c> column and keeping room for a genuinely different trigger
/// shape later without another schema/call-site change.
/// </summary>
public enum ExecutionMode
{
    Local
}

/// <summary>The terminal outcome of an execution.</summary>
public enum ExecutionOutcome
{
    Success,
    Partial,
    Failed
}

/// <summary>Lifecycle state of an execution.</summary>
public enum ExecutionState
{
    Running,
    Completed,
    Failed
}

/// <summary>Quantitative summary of an execution — a value object carried by the execution record.</summary>
public sealed record ExecutionMetrics(
    TimeSpan Duration,
    int DiscoveriesEmitted,
    int DiscoveriesAccepted,
    int NodesChanged,
    int RelationshipsChanged)
{
    public static ExecutionMetrics Empty => new(TimeSpan.Zero, 0, 0, 0, 0);
}

/// <summary>
/// Aggregate root of the Analysis sub-domain: one act of analysis over an application, with a
/// guarded lifecycle (Running → Completed | Failed). Collects diagnostics and produces the durable
/// outcome. Identity equality is by <see cref="Id"/>. The aggregate owns its state transitions —
/// that behavior is domain logic, not infrastructure.
/// </summary>
public sealed class AnalysisExecution : IEquatable<AnalysisExecution>
{
    private readonly List<Diagnostic> _diagnostics = new();

    public ExecutionId Id { get; }
    public ApplicationId Application { get; }
    public ExecutionMode Mode { get; }
    public DateTimeOffset StartedAt { get; }
    public ExecutionState State { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public ExecutionOutcome? Outcome { get; private set; }
    public SnapshotId? ResultingSnapshot { get; private set; }
    public ExecutionMetrics Metrics { get; private set; } = ExecutionMetrics.Empty;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    private AnalysisExecution(ExecutionId id, ApplicationId application, ExecutionMode mode, DateTimeOffset startedAt)
    {
        Id = id;
        Application = application;
        Mode = mode;
        StartedAt = startedAt;
        State = ExecutionState.Running;
    }

    public static AnalysisExecution Start(ExecutionId id, ApplicationId application, ExecutionMode mode, DateTimeOffset startedAt) =>
        new(id, application, mode, startedAt);

    /// <summary>Record a diagnostic. Only permitted while the execution is running.</summary>
    public void Report(Diagnostic diagnostic)
    {
        Guard.Requires(State == ExecutionState.Running, "Cannot report diagnostics on a finished execution.");
        _diagnostics.Add(diagnostic);
    }

    /// <summary>Complete the execution successfully (or partially), sealing its result.</summary>
    public void Complete(ExecutionOutcome outcome, SnapshotId snapshot, ExecutionMetrics metrics, DateTimeOffset completedAt)
    {
        Guard.Requires(State == ExecutionState.Running, "Execution has already finished.");
        Guard.Requires(outcome != ExecutionOutcome.Failed, "Use Fail() for a failed execution.");
        State = ExecutionState.Completed;
        Outcome = outcome;
        ResultingSnapshot = snapshot;
        Metrics = metrics;
        CompletedAt = completedAt;
    }

    /// <summary>Fail the execution. No snapshot is produced.</summary>
    public void Fail(ExecutionMetrics metrics, DateTimeOffset failedAt)
    {
        Guard.Requires(State == ExecutionState.Running, "Execution has already finished.");
        State = ExecutionState.Failed;
        Outcome = ExecutionOutcome.Failed;
        Metrics = metrics;
        CompletedAt = failedAt;
    }

    public bool Equals(AnalysisExecution? other) => other is not null && Id.Equals(other.Id);

    public override bool Equals(object? obj) => Equals(obj as AnalysisExecution);

    public override int GetHashCode() => Id.GetHashCode();
}
