namespace Aip.Abstractions.History;

/// <summary>One repository's commit delta as part of a published version.</summary>
public sealed record RepositoryCommitChange(string RepositoryName, string? PreviousCommit, string NewCommit);

/// <summary>
/// What changed in a published documentation version compared to the one before it — the persisted
/// counterpart of the in-memory <c>SnapshotDiff</c> (Aip.Core.Domain.SnapshotDiff), captured once per
/// publish so the Viewer can show "what changed" without re-diffing snapshots on every page view. Never
/// recorded for a version with no predecessor (v1) — there's nothing to compare against.
/// </summary>
public sealed record DocumentVersionChange(
    string Application,
    int VersionNumber,
    int PreviousVersionNumber,
    int NodesAdded,
    int NodesRemoved,
    int RelationshipsAdded,
    int RelationshipsRemoved,
    IReadOnlyList<string> AddedNodeNames,
    IReadOnlyList<string> RemovedNodeNames,
    IReadOnlyList<string> AddedRelationshipNames,
    IReadOnlyList<string> RemovedRelationshipNames,
    IReadOnlyList<RepositoryCommitChange> RepositoryCommits,
    string Summary,
    bool AiWritten,
    DateTimeOffset OccurredAt);

/// <summary>
/// Durable record of what changed between consecutive published documentation versions, backing the
/// Viewer's "What Changed" page. Separate from Run History (whole-run outcomes) and AiFallbackEvents
/// (per-page AI failures) — this is specifically the version-to-version documentation delta.
/// </summary>
public interface IVersionChangeStore
{
    Task RecordAsync(DocumentVersionChange change, CancellationToken ct = default);

    Task<DocumentVersionChange?> GetAsync(string application, int versionNumber, CancellationToken ct = default);
}
