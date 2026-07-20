using System.Collections.Concurrent;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Observability;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Observability;

/// <summary>In-memory metrics collector (counters + gauges). No silent runs — everything is measurable.</summary>
public sealed class MetricsCollector : IMetricsCollector
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();

    public void Increment(string name, long value = 1) => _counters.AddOrUpdate(name, value, (_, e) => e + value);
    public void Record(string name, double value) => _gauges[name] = value;

    public IReadOnlyDictionary<string, long> Counters => _counters;
    public IReadOnlyDictionary<string, double> Gauges => _gauges;
}

/// <summary>Reports execution results into the metrics collector (plugin, token, knowledge-growth metrics).</summary>
internal sealed class ExecutionReporter : IExecutionReporter
{
    private readonly MetricsCollector _metrics;

    public ExecutionReporter(MetricsCollector metrics) => _metrics = metrics;

    public Task ReportAsync(ExecutionResult result, CancellationToken ct = default)
    {
        _metrics.Increment("executions.total");
        _metrics.Increment($"executions.outcome.{result.Outcome}");
        _metrics.Record("last.discoveries", result.Discoveries.Count);
        _metrics.Record("last.nodes_changed", result.Metrics.NodesChanged);
        _metrics.Record("last.relationships_changed", result.Metrics.RelationshipsChanged);
        _metrics.Record("last.duration_ms", result.Metrics.Duration.TotalMilliseconds);
        _metrics.Record("last.diagnostics", result.Diagnostics.Count);

        return Task.CompletedTask;
    }
}

public static class ObservabilityModule
{
    public static IServiceCollection AddAipObservability(this IServiceCollection services)
    {
        services.AddSingleton<MetricsCollector>();
        services.AddSingleton<IMetricsCollector>(sp => sp.GetRequiredService<MetricsCollector>());
        services.AddSingleton<IExecutionReporter, ExecutionReporter>();

        return services;
    }
}
