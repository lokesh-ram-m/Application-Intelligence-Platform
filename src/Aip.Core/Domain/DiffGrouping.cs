namespace Aip.Core.Domain;

/// <summary>
/// Groups a <see cref="SnapshotDiff"/> by which application each item actually belongs to (via
/// <see cref="KnowledgeIdentity"/>'s "app:" root segment — see <see cref="KnowledgeExtensions.OwningApplication"/>)
/// — the shared logic behind both the version-changelog's per-app breakdown and
/// <c>DocumentVersionChange.PerApplicationImpact</c>. For a leaf application every item belongs to itself,
/// so grouping degrades to a single entry; callers should treat that as "nothing to break out."
/// </summary>
public static class DiffGrouping
{
    public sealed record ApplicationImpact(
        string Application,
        IReadOnlyList<KnowledgeNode> AddedNodes,
        IReadOnlyList<KnowledgeNode> RemovedNodes,
        IReadOnlyList<Relationship> AddedRelationships,
        IReadOnlyList<Relationship> RemovedRelationships);

    public static IReadOnlyList<ApplicationImpact> GroupByOwningApplication(SnapshotDiff diff)
    {
        var apps = new HashSet<string>();
        foreach (KnowledgeNode n in diff.AddedNodes.Concat(diff.RemovedNodes))
            if (n.Identity.OwningApplication() is { } owner) apps.Add(owner);
        // A relationship is attributed to its source's owning application — "which app initiated this" is
        // the more useful framing than double-counting into both endpoints.
        foreach (Relationship r in diff.AddedRelationships.Concat(diff.RemovedRelationships))
            if (r.From.OwningApplication() is { } owner) apps.Add(owner);

        return apps.OrderBy(a => a, StringComparer.Ordinal).Select(app => new ApplicationImpact(
            app,
            diff.AddedNodes.Where(n => n.Identity.OwningApplication() == app).ToList(),
            diff.RemovedNodes.Where(n => n.Identity.OwningApplication() == app).ToList(),
            diff.AddedRelationships.Where(r => r.From.OwningApplication() == app).ToList(),
            diff.RemovedRelationships.Where(r => r.From.OwningApplication() == app).ToList())
        ).ToList();
    }

    /// <summary>
    /// Relationships whose two endpoints belong to DIFFERENT owning applications — the single most useful
    /// fact for a composite application's "what changed": a genuinely new (or removed) integration between
    /// two of its sub-applications, not just an internal change within one of them.
    /// </summary>
    public static (IReadOnlyList<Relationship> Added, IReadOnlyList<Relationship> Removed) CrossApplicationRelationships(SnapshotDiff diff)
    {
        bool IsCrossApp(Relationship r) => r.From.OwningApplication() != r.To.OwningApplication();

        return (diff.AddedRelationships.Where(IsCrossApp).ToList(), diff.RemovedRelationships.Where(IsCrossApp).ToList());
    }

    /// <summary>
    /// True when the diff contains at least one item whose owning application differs from
    /// <paramref name="application"/> itself — i.e. this publish actually pulled in a child's changes, not
    /// just the application's own. This is the correct "is this a genuinely composite publish" signal:
    /// grouped-count alone is NOT enough, since a composite whose diff this run only touches ONE child also
    /// produces exactly one group — indistinguishable by count from an ordinary leaf application's diff.
    /// </summary>
    public static bool IsCompositeImpact(SnapshotDiff diff, string application) =>
        GroupByOwningApplication(diff).Any(a => a.Application != application);
}
