using System.Collections.Concurrent;

using Aip.Abstractions.Plugins;
using Aip.Abstractions.Registries;
using Aip.Core.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Registries;

/// <summary>
/// The Application Registry — the estate catalog. In-memory and seedable for now; repository
/// self-declaration and persistence arrive in a later milestone.
/// </summary>
public sealed class SeedableApplicationRegistry : IApplicationRegistry
{
    private readonly ConcurrentDictionary<string, ApplicationDescriptor> _applications = new();

    public void Register(ApplicationDescriptor descriptor) => _applications[descriptor.Name] = descriptor;

    public Task<IReadOnlyList<ApplicationDescriptor>> GetApplicationsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApplicationDescriptor>>(_applications.Values.ToList());
}

/// <summary>
/// The Schema Registry — governs the node vocabulary consulted by Validation. The catalog is the union of
/// every loaded plugin's own declared <see cref="PluginManifest.Capabilities"/> (a plugin's manifest is
/// the single source of truth for which kinds it may legitimately emit — new vocabulary arrives with a
/// plugin, never a Core change) plus the small, fixed set of <see cref="CoreNodeKinds"/> that are always
/// registered regardless of which plugins are loaded.
/// </summary>
internal sealed class InMemorySchemaRegistry : ISchemaRegistry
{
    private const string CoreNamespace = "core";

    private readonly IPluginRegistry _plugins;

    public InMemorySchemaRegistry(IPluginRegistry plugins) => _plugins = plugins;

    public async Task<IReadOnlyList<NodeKindDefinition>> GetNodeKindsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PluginManifest> manifests = await _plugins.GetManifestsAsync(ct);

        return manifests
            .SelectMany(m => m.Capabilities.Select(kind => new NodeKindDefinition(kind, m.Id)))
            .Concat(new[] { new NodeKindDefinition(CoreNodeKinds.External, CoreNamespace) })
            .DistinctBy(d => d.Kind)
            .ToList();
    }

    public async Task<bool> IsRegisteredAsync(string kind, CancellationToken ct = default)
    {
        IReadOnlyList<NodeKindDefinition> kinds = await GetNodeKindsAsync(ct);

        return kinds.Any(d => d.Kind == kind);
    }
}

/// <summary>The Plugin Registry — the catalog of loaded plugin manifests, sourced from the Plugin Host.</summary>
internal sealed class PluginRegistry : IPluginRegistry
{
    private readonly IPluginHost _host;

    public PluginRegistry(IPluginHost host) => _host = host;

    public Task<IReadOnlyList<PluginManifest>> GetManifestsAsync(CancellationToken ct = default)
        => Task.FromResult(_host.Manifests);
}

public static class RegistriesModule
{
    public static IServiceCollection AddAipRegistries(this IServiceCollection services)
    {
        services.AddSingleton<SeedableApplicationRegistry>();
        services.AddSingleton<IApplicationRegistry>(sp => sp.GetRequiredService<SeedableApplicationRegistry>());
        services.AddSingleton<ISchemaRegistry, InMemorySchemaRegistry>();
        services.AddSingleton<IPluginRegistry, PluginRegistry>();

        return services;
    }
}
