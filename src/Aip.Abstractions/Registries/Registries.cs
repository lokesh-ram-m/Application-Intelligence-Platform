using Aip.Abstractions.Plugins;

namespace Aip.Abstractions.Registries;

/// <summary>
/// An application and the repositories that compose it (the estate catalog). When
/// <paramref name="SkipIfUnchanged"/> is true, a batch run (<c>apps.yml</c>, no explicit changed files)
/// checks each repository's current commit against the last one recorded in Run History
/// (<c>IRunHistoryStore.GetLastCommitAsync</c>) and skips the whole run — no analysis, no AI cost — when
/// every repository is unchanged since its last analyzed commit. Defaults to false (always re-run),
/// preserving prior behavior.
/// </summary>
public sealed record ApplicationDescriptor(string Name, IReadOnlyList<string> Repositories, bool SkipIfUnchanged = false);

/// <summary>The Application Registry — what applications exist and which repos map to them.</summary>
public interface IApplicationRegistry
{
    Task<IReadOnlyList<ApplicationDescriptor>> GetApplicationsAsync(CancellationToken ct = default);
}

/// <summary>A governed node-kind definition (the vocabulary is data, not a Core enum — Session 2 §7).</summary>
public sealed record NodeKindDefinition(string Kind, string Namespace);

/// <summary>The Schema Registry — governs the node-kind / relationship vocabulary consulted by Validation.</summary>
public interface ISchemaRegistry
{
    Task<IReadOnlyList<NodeKindDefinition>> GetNodeKindsAsync(CancellationToken ct = default);
    Task<bool> IsRegisteredAsync(string kind, CancellationToken ct = default);
}

/// <summary>The Plugin Registry — the catalog of installed plugins and their manifests.</summary>
public interface IPluginRegistry
{
    Task<IReadOnlyList<PluginManifest>> GetManifestsAsync(CancellationToken ct = default);
}
