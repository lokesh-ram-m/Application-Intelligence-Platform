namespace Aip.Core.Domain;

/// <summary>
/// A durable, identified fact about the estate. Created only through <see cref="Create"/>, which
/// enforces the domain invariant that every fact must carry Evidence.
/// </summary>
public sealed record KnowledgeNode
{
    public KnowledgeIdentity Identity { get; }
    public NodeKind Kind { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
    public IReadOnlyList<Evidence> Evidence { get; }
    public Confidence Confidence { get; }

    private KnowledgeNode(
        KnowledgeIdentity identity,
        NodeKind kind,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence)
    {
        Identity = identity;
        Kind = kind;
        Properties = properties;
        Evidence = evidence;
        Confidence = confidence;
    }

    public static KnowledgeNode Create(
        KnowledgeIdentity identity,
        NodeKind kind,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Guard.NotEmpty(evidence, nameof(evidence));

        return new KnowledgeNode(
            identity,
            kind,
            properties ?? EmptyProperties,
            evidence,
            confidence);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new Dictionary<string, string>();
}

/// <summary>
/// A durable, typed, directional connection between two Knowledge Nodes. Enforces that a
/// relationship carries Evidence and connects two distinct endpoints.
/// </summary>
public sealed record Relationship
{
    public RelationshipType Type { get; }
    public KnowledgeIdentity From { get; }
    public KnowledgeIdentity To { get; }
    public IReadOnlyList<Evidence> Evidence { get; }
    public Confidence Confidence { get; }

    private Relationship(
        RelationshipType type,
        KnowledgeIdentity from,
        KnowledgeIdentity to,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence)
    {
        Type = type;
        From = from;
        To = to;
        Evidence = evidence;
        Confidence = confidence;
    }

    public static Relationship Create(
        RelationshipType type,
        KnowledgeIdentity from,
        KnowledgeIdentity to,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence)
    {
        Guard.NotEmpty(evidence, nameof(evidence));
        Guard.Requires(!from.Equals(to), "A relationship must connect two distinct nodes.");

        return new Relationship(type, from, to, evidence, confidence);
    }
}

/// <summary>
/// Small read helpers shared by resolvers and projections — a single canonical place instead of every
/// consumer reimplementing "look up a property with a fallback" or "a short display label" for the same
/// two domain types.
/// </summary>
public static class KnowledgeExtensions
{
    public static string? Prop(this KnowledgeNode node, string key) =>
        node.Properties.TryGetValue(key, out string? v) ? v : null;

    public static string Label(this KnowledgeNode node) =>
        $"{node.Prop("name") ?? node.Identity.ShortName} ({node.Kind.Value})";

    public static string Label(this Relationship relationship) =>
        $"{relationship.Type.Value} {relationship.From.ShortName} → {relationship.To.ShortName}";

    // The "app:" segment is always the root of a KnowledgeIdentity (see KnowledgeIdentity.ForApplication)
    // and is stamped in at analysis time by whichever application originally analyzed the node — so for a
    // composite application's pooled node set, this recovers "which application does this fact actually
    // belong to" (itself, or a child it pulled a snapshot from) with no separate tracking needed.
    public static string? OwningApplication(this KnowledgeIdentity identity) =>
        identity.Segments.Count > 0 && identity.Segments[0].Kind == "app" ? identity.Segments[0].Value : null;
}
