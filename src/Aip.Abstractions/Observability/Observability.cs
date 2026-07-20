using Aip.Abstractions.Analysis;

namespace Aip.Abstractions.Observability;

/// <summary>Collects platform metrics (throughput, incremental efficiency, accept/reject rates). No silent runs.</summary>
public interface IMetricsCollector
{
    void Increment(string name, long value = 1);
    void Record(string name, double value);
}

/// <summary>Emits the full execution result for observability, cost, and troubleshooting.</summary>
public interface IExecutionReporter
{
    Task ReportAsync(ExecutionResult result, CancellationToken ct = default);
}

/// <summary>
/// The durable ledger of execution results — the authority for incremental decisions
/// ("has this commit already been analyzed by these plugin versions?") and observability (Session 4 §1).
/// The concrete store lives in Infrastructure.
/// </summary>
public interface IExecutionStore
{
    Task SaveAsync(ExecutionResult result, CancellationToken ct = default);
    Task<ExecutionResult?> GetAsync(Guid executionId, CancellationToken ct = default);
}
