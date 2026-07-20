using Aip.Abstractions.Engines;
using Aip.Core.Domain;

namespace Aip.Abstractions.Analysis;

/// <summary>
/// The immutable scope of one execution — the "what am I analyzing" facts, safe to share with every
/// plugin. Plugins see the PREVIOUS snapshot (read-only baseline), never the in-flight one.
/// </summary>
public sealed record ExecutionScope(
    ApplicationId Application,
    IReadOnlyList<RepositoryId> Repositories,
    IReadOnlyList<string> ChangedFiles,
    ExecutionMode Mode,
    Snapshot? PreviousSnapshot);

/// <summary>
/// The only channel by which analyzers emit facts. Analyzers propose Discoveries; they never write
/// Knowledge directly. Diagnostics record limits honestly.
/// </summary>
public interface IDiscoverySink
{
    void Add(Discovery discovery);
    void Report(Diagnostic diagnostic);
}

/// <summary>
/// The ambient state handed to every analyzer: the artifact under analysis, its semantic model, and
/// deterministic helpers for minting identities and evidence — so analyzers stay stateless and
/// consistent. Identity is always minted here, never hand-rolled by a plugin.
/// </summary>
public interface IAnalysisContext
{
    ExecutionId ExecutionId { get; }
    ExecutionScope Scope { get; }
    Artifact Artifact { get; }
    RepositoryId Repository { get; }
    Commit Commit { get; }
    string Engine { get; }
    ISemanticModel Model { get; }

    /// <summary>Mint a node identity scoped to this application + repository (app/repo/… hierarchy).</summary>
    KnowledgeIdentity NodeId(params IdentitySegment[] tail);

    /// <summary>Mint an application-scoped identity (e.g. an Endpoint contract shared across repos).</summary>
    KnowledgeIdentity AppNodeId(params IdentitySegment[] tail);

    /// <summary>Create deterministic evidence anchored at the current repository/commit/engine.</summary>
    Evidence Evidence(string? file = null, int? line = null, string? symbol = null);
}
