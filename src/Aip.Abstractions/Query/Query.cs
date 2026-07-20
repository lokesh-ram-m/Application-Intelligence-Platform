using Aip.Core.Domain;

namespace Aip.Abstractions.Query;

/// <summary>
/// The read interface over the Knowledge Model — the strategic long-term product surface that
/// documentation, search, UI, and a future AI assistant all consume. Consumers speak Knowledge.
/// </summary>
public interface IKnowledgeQueryService
{
    Task<KnowledgeNode?> GetNodeAsync(ApplicationId application, KnowledgeIdentity identity, CancellationToken ct = default);
    Task<IReadOnlyList<Relationship>> GetRelationshipsAsync(ApplicationId application, KnowledgeIdentity from, CancellationToken ct = default);
}

/// <summary>A neighbour of a node reached across one relationship (for traversal/impact results).</summary>
public sealed record GraphHop(RelationshipType Via, KnowledgeIdentity Node, string Kind);

/// <summary>
/// The Query Platform: graph traversal, impact analysis, snapshot queries, and search over the
/// Knowledge Model. Everything reads a snapshot; nothing reaches past the model into storage or source.
/// </summary>
public interface IQueryPlatform
{
    Task<IReadOnlyList<GraphHop>> TraverseAsync(ApplicationId application, KnowledgeIdentity from, int depth = 1, CancellationToken ct = default);

    /// <summary>Everything that transitively depends on / reaches a node — "what breaks if I change this?".</summary>
    Task<IReadOnlyList<KnowledgeIdentity>> ImpactAsync(ApplicationId application, KnowledgeIdentity target, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeNode>> SearchAsync(ApplicationId application, string term, CancellationToken ct = default);

    Task<IReadOnlyList<SnapshotId>> SnapshotsAsync(ApplicationId application, CancellationToken ct = default);
}
