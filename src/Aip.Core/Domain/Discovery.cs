namespace Aip.Core.Domain;

/// <summary>
/// A proposed, unvalidated fact emitted by an analyzer or resolver. Transient: it passes through the
/// single Validation gate before becoming Knowledge. Origin (deterministic vs probabilistic) does not
/// change governance. Every Discovery must carry Evidence.
/// </summary>
public abstract record Discovery
{
    public IReadOnlyList<Evidence> Evidence { get; }
    public Confidence Confidence { get; }

    protected Discovery(IReadOnlyList<Evidence> evidence, Confidence confidence)
    {
        Evidence = Guard.NotEmpty(evidence, nameof(evidence));
        Confidence = confidence;
    }
}

/// <summary>A proposal to create or refine a Knowledge Node.</summary>
public sealed record NodeDiscovery : Discovery
{
    public KnowledgeIdentity Identity { get; }
    public NodeKind Kind { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }

    private NodeDiscovery(
        KnowledgeIdentity identity,
        NodeKind kind,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence) : base(evidence, confidence)
    {
        Identity = identity;
        Kind = kind;
        Properties = properties;
    }

    public static NodeDiscovery Create(
        KnowledgeIdentity identity,
        NodeKind kind,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence,
        IReadOnlyDictionary<string, string>? properties = null) =>
        new(identity, kind, properties ?? new Dictionary<string, string>(), evidence, confidence);
}

/// <summary>A proposal to create a Relationship (also what Relationship Resolvers emit).</summary>
public sealed record RelationshipDiscovery : Discovery
{
    public RelationshipType Type { get; }
    public KnowledgeIdentity From { get; }
    public KnowledgeIdentity To { get; }

    private RelationshipDiscovery(
        RelationshipType type,
        KnowledgeIdentity from,
        KnowledgeIdentity to,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence) : base(evidence, confidence)
    {
        Type = type;
        From = from;
        To = to;
    }

    public static RelationshipDiscovery Create(
        RelationshipType type,
        KnowledgeIdentity from,
        KnowledgeIdentity to,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence)
    {
        Guard.Requires(!from.Equals(to), "A relationship must connect two distinct nodes.");

        return new RelationshipDiscovery(type, from, to, evidence, confidence);
    }
}
