using Aip.Abstractions.Analysis;

namespace Aip.Abstractions.Plugins;

/// <summary>
/// A plugin's self-declaration, used by the Plugin Host for discovery, routing, ordering and
/// dependency resolution — without the host knowing any plugin by name.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Version,
    IReadOnlyList<string> SupportedArtifacts,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Capabilities,
    int Priority,
    IReadOnlyList<string> Dependencies)
{
    public string Language => Languages.Count > 0 ? Languages[0] : string.Empty;

    public bool Claims(string technology) => SupportedArtifacts.Contains(technology);
}

/// <summary>
/// A Technology Plugin: a bundle of framework knowledge that consumes a Language Engine and emits
/// Discoveries via analyzers. Plugins are implementation-independent and replaceable.
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }
    Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default);
}

/// <summary>A single focused extraction pass within a plugin. Deterministic, idempotent, order-independent.</summary>
public interface IAnalyzer
{
    string Name { get; }
    Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default);
}

/// <summary>
/// Discovers/loads plugins, resolves their execution order (dependencies + priority), and routes each
/// artifact to the plugins that claim it — the composition point of the plugin framework.
/// </summary>
public interface IPluginHost
{
    /// <summary>Plugins that claim an artifact, in dependency-then-priority order.</summary>
    IReadOnlyList<IPlugin> SelectFor(Artifact artifact);

    /// <summary>All loaded plugin manifests (the plugin registry's source of truth).</summary>
    IReadOnlyList<PluginManifest> Manifests { get; }
}
