using Aip.Abstractions.Plugins;

namespace Aip.Abstractions.Registries;

/// <summary>
/// An application and the repositories that compose it (the estate catalog). When
/// <paramref name="SkipIfUnchanged"/> is true, a batch run (<c>apps.yml</c>, no explicit changed files)
/// checks each repository's current commit against the last one recorded in Run History
/// (<c>IRunHistoryStore.GetLastCommitAsync</c>) and skips the whole run — no analysis, no AI cost — when
/// every repository is unchanged since its last analyzed commit. Defaults to false (always re-run),
/// preserving prior behavior.
///
/// <paramref name="Children"/> names other applications (declared elsewhere in the same estate) whose
/// Knowledge Models are merged into this one — a composite application. A node with both
/// <see cref="Repositories"/> and <see cref="Children"/> analyzes its own code and folds in every child's
/// latest snapshot; a pure composite has no repositories of its own. Defaults to empty, preserving prior
/// (flat, leaf-only) behavior.
///
/// <paramref name="ForceReanalysis"/> is the opposite knob from <see cref="SkipIfUnchanged"/> and operates
/// one layer deeper: even when every repository's commit is unchanged (so the whole-app run wouldn't be
/// skipped, or <see cref="SkipIfUnchanged"/> is false anyway), the pipeline's per-artifact incremental
/// pruning normally still treats an unchanged commit as "nothing to re-analyze" and prunes every artifact.
/// Setting this to true bypasses that pruning for this application — every repository is treated as fully
/// changed regardless of its actual commit, so every artifact is re-analyzed from scratch. Useful right
/// after deploying new/changed analyzers, when you want fresh facts from existing code without needing a
/// real upstream commit. Defaults to false, preserving today's incremental-pruning behavior. Setting both
/// this and <see cref="SkipIfUnchanged"/> to true is contradictory — <see cref="SkipIfUnchanged"/> is
/// checked first and wins, since it skips the run before this would ever come into play.
/// </summary>
public sealed record ApplicationDescriptor(string Name, IReadOnlyList<string> Repositories, bool SkipIfUnchanged = false, IReadOnlyList<string>? Children = null, bool ForceReanalysis = false)
{
    public IReadOnlyList<string> Children { get; init; } = Children ?? Array.Empty<string>();
}

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
