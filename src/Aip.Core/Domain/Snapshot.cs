namespace Aip.Core.Domain;

/// <summary>
/// Aggregate root of the Knowledge sub-domain: a consistent, versioned state of an application's
/// Knowledge Model at a point in time. The factory enforces the graph invariants — node identities
/// are unique, and every relationship endpoint refers to a node present in the snapshot.
/// Identity equality is by <see cref="Id"/>.
/// </summary>
public sealed class Snapshot : IEquatable<Snapshot>
{
    public SnapshotId Id { get; }
    public ApplicationId Application { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<KnowledgeNode> Nodes { get; }
    public IReadOnlyList<Relationship> Relationships { get; }

    private Snapshot(
        SnapshotId id,
        ApplicationId application,
        DateTimeOffset createdAt,
        IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyList<Relationship> relationships)
    {
        Id = id;
        Application = application;
        CreatedAt = createdAt;
        Nodes = nodes;
        Relationships = relationships;
    }

    public static Snapshot Create(
        SnapshotId id,
        ApplicationId application,
        DateTimeOffset createdAt,
        IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyList<Relationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(relationships);

        var identities = new HashSet<KnowledgeIdentity>();
        foreach (KnowledgeNode node in nodes)
        {
            if (!identities.Add(node.Identity))
                throw new DomainException($"Duplicate node identity in snapshot: {node.Identity}.");
        }

        foreach (Relationship relationship in relationships)
        {
            if (!identities.Contains(relationship.From))
                throw new DomainException($"Relationship '{relationship.Type}' references unknown source node {relationship.From}.");
            if (!identities.Contains(relationship.To))
                throw new DomainException($"Relationship '{relationship.Type}' references unknown target node {relationship.To}.");
        }

        return new Snapshot(id, application, createdAt, nodes, relationships);
    }

    public KnowledgeNode? FindNode(KnowledgeIdentity identity) =>
        Nodes.FirstOrDefault(n => n.Identity.Equals(identity));

    public bool Equals(Snapshot? other) => other is not null && Id.Equals(other.Id);

    public override bool Equals(object? obj) => Equals(obj as Snapshot);

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// The delta between two snapshots — the primitive behind incremental projection and notifications.
/// A value object describing change; it has no identity of its own.
/// </summary>
public sealed record SnapshotDiff(
    SnapshotId From,
    SnapshotId To,
    IReadOnlyList<KnowledgeNode> AddedNodes,
    IReadOnlyList<KnowledgeNode> RemovedNodes,
    IReadOnlyList<Relationship> AddedRelationships,
    IReadOnlyList<Relationship> RemovedRelationships)
{
    /// <summary>True when neither nodes nor relationships changed at all between the two snapshots.</summary>
    public bool IsEmpty => AddedNodes.Count == 0 && RemovedNodes.Count == 0
        && AddedRelationships.Count == 0 && RemovedRelationships.Count == 0;
}

/// <summary>
/// Pure diffing over two snapshots — by node identity and relationship key, producing plain added/removed
/// sets (a renamed identity is reported as one removal plus one addition; nothing here attempts rename
/// detection). Lives in Core rather than a specific storage adapter since it operates only on in-memory
/// <see cref="Snapshot"/> values and every <see cref="IKnowledgeRepository"/> implementation needs it
/// identically, regardless of where snapshots are actually persisted.
/// </summary>
public static class SnapshotDiffing
{
    public static SnapshotDiff Diff(Snapshot a, Snapshot b)
    {
        var aNodes = a.Nodes.ToDictionary(n => n.Identity);
        var bNodes = b.Nodes.ToDictionary(n => n.Identity);
        var added = b.Nodes.Where(n => !aNodes.ContainsKey(n.Identity)).ToList();
        var removed = a.Nodes.Where(n => !bNodes.ContainsKey(n.Identity)).ToList();

        static string Key(Relationship r) => $"{r.Type.Value}|{r.From.Value}|{r.To.Value}";
        var aRel = a.Relationships.ToDictionary(Key);
        var bRel = b.Relationships.ToDictionary(Key);
        var addedRel = b.Relationships.Where(r => !aRel.ContainsKey(Key(r))).ToList();
        var removedRel = a.Relationships.Where(r => !bRel.ContainsKey(Key(r))).ToList();

        return new SnapshotDiff(a.Id, b.Id, added, removed, addedRel, removedRel);
    }
}
