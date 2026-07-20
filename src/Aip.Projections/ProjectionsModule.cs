using Aip.Abstractions.Projections;
using Aip.Core.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Projections;

/// <summary>Runs the registered projections against a snapshot. Never coupled to any single output.</summary>
internal sealed class ProjectionEngine : IProjectionEngine
{
    private readonly IReadOnlyList<IProjection> _projections;

    public ProjectionEngine(IEnumerable<IProjection> projections) => _projections = projections.ToList();

    public async Task<IReadOnlyList<ProjectionResult>> RunAsync(Snapshot snapshot, IReadOnlyList<string>? repositories = null, CancellationToken ct = default)
    {
        var request = new ProjectionRequest(snapshot, repositories ?? Array.Empty<string>());
        var results = new List<ProjectionResult>();
        foreach (IProjection projection in _projections)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await projection.ProjectAsync(request, ct));
        }

        return results;
    }
}

public static class ProjectionsModule
{
    public static IServiceCollection AddAipProjections(this IServiceCollection services)
    {
        services.AddSingleton<IProjection, DocumentationProjection>();
        services.AddSingleton<IProjectionEngine, ProjectionEngine>();
        services.AddSingleton<IVersionChangelogGenerator, VersionChangelogGenerator>();

        return services;
    }
}
