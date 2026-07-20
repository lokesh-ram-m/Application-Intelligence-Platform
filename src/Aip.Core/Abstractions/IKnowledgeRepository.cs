using Aip.Core.Domain;

namespace Aip.Core.Abstractions;

/// <summary>
/// The Core PORT for the Knowledge Store. The Core depends only on this contract, never on a
/// storage technology — so the store can migrate (relational → graph DB) without a Core change
/// (Platform Architecture, Session 4 §2). Concrete adapters live in Infrastructure.
/// </summary>
public interface IKnowledgeRepository
{
    /// <summary>The latest sealed snapshot for an application, or null if none exists yet.</summary>
    Task<Snapshot?> GetSnapshotAsync(ApplicationId application, CancellationToken ct = default);

    /// <summary>Append validated knowledge as a new snapshot (append-only; never a destructive write).</summary>
    Task<Snapshot> CommitAsync(
        ApplicationId application,
        IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyList<Relationship> relationships,
        CancellationToken ct = default);

    /// <summary>Diff two snapshots — the primitive behind incremental projection and notifications.</summary>
    Task<SnapshotDiff> DiffAsync(SnapshotId from, SnapshotId to, CancellationToken ct = default);
}
