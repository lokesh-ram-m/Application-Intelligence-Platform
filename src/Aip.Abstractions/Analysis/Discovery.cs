using Aip.Core.Domain;

namespace Aip.Abstractions.Analysis;

/// <summary>
/// A classified unit of source discovered inside a repository (a .NET project, an Angular workspace).
/// The unit a plugin claims and an analyzer runs over.
/// </summary>
public sealed record Artifact(
    RepositoryId Repository,
    string Path,
    string Technology,
    string Name);

/// <summary>Materializes a repository locally at a known commit (git clone/fetch or local path passthrough).</summary>
public interface IRepositorySource
{
    /// <summary>
    /// Materializes a repository at its current HEAD. When <paramref name="previousCommit"/> is supplied
    /// and differs from the resolved HEAD, the adapter attempts to compute which files changed between the
    /// two commits — see <see cref="RepositoryMaterialization.ChangedFiles"/> for how "unknown" is
    /// represented when that isn't possible.
    /// </summary>
    Task<RepositoryMaterialization> MaterializeAsync(RepositoryId repository, string location, string? previousCommit = null, CancellationToken ct = default);
}

/// <summary>How a repository was sourced — local path, a public git URL, or a git URL authenticated with a configured PAT.</summary>
public enum RepositorySourceKind
{
    Local,
    PublicGit,
    PrivateGit
}

/// <summary>The local working copy of a repository at a known commit.</summary>
/// <param name="ChangedFiles">
/// File names changed since <c>previousCommit</c> (the parameter passed to MaterializeAsync), or null when
/// that isn't known — either no previous commit was supplied (first time this repository is analyzed), the
/// commit didn't change, or the diff itself couldn't be computed (e.g. the previous commit is no longer
/// reachable after a force-push/rebase). Null is deliberately distinct from an empty list ("diffed cleanly,
/// genuinely nothing changed") — callers must treat null as "assume everything under this repository
/// changed", never as "nothing changed", since silently under-reporting a real change would corrupt
/// incremental analysis.
/// </param>
public sealed record RepositoryMaterialization(RepositoryId Repository, string RootPath, Commit Commit, RepositorySourceKind SourceKind, IReadOnlyList<string>? ChangedFiles = null);

/// <summary>Walks a materialized repository and classifies its files into Artifacts.</summary>
public interface IArtifactDiscovery
{
    Task<IReadOnlyList<Artifact>> DiscoverAsync(RepositoryId repository, string rootPath, CancellationToken ct = default);
}
