using System.Text.Json;

using Aip.Core.Domain;

namespace Aip.Infrastructure;

// Domain objects have private constructors and invariant-enforcing factories, so nodes/relationships are
// serialized as flat DTOs and rebuilt through those factories on load.
internal sealed record NodeDto(string Identity, string Kind, double Confidence, Dictionary<string, string> Properties, List<EvidenceDto> Evidence);
internal sealed record RelationshipDto(string Type, string From, string To, double Confidence, List<EvidenceDto> Evidence);
internal sealed record EvidenceDto(string Repository, string Commit, string Engine, string Method, double Confidence, string? File, int? Line, string? Symbol);

/// <summary>
/// JSON conversion between a Snapshot's nodes/relationships and their DTO shape — shared by every
/// <see cref="Aip.Abstractions.Knowledge.IKnowledgeStore"/> implementation that needs to serialize a
/// snapshot (today: <see cref="EfKnowledgeRepository"/>, storing one JSON column per list).
/// </summary>
internal static class SnapshotSerialization
{
    private static readonly JsonSerializerOptions Json = new();

    public static string SerializeNodes(IReadOnlyList<KnowledgeNode> nodes) =>
        JsonSerializer.Serialize(nodes.Select(ToDto).ToList(), Json);

    public static string SerializeRelationships(IReadOnlyList<Relationship> relationships) =>
        JsonSerializer.Serialize(relationships.Select(ToDto).ToList(), Json);

    // A corrupt/unreadable column is treated as empty rather than crashing the run — mirrors the
    // defensive posture the old file-based store used for a corrupt file.
    public static List<KnowledgeNode> DeserializeNodes(string json)
    {
        try { return (JsonSerializer.Deserialize<List<NodeDto>>(json, Json) ?? new()).Select(ToDomain).ToList(); }
        catch (JsonException) { return new List<KnowledgeNode>(); }
    }

    public static List<Relationship> DeserializeRelationships(string json)
    {
        try { return (JsonSerializer.Deserialize<List<RelationshipDto>>(json, Json) ?? new()).Select(ToDomain).ToList(); }
        catch (JsonException) { return new List<Relationship>(); }
    }

    // ---- domain → dto ----
    private static NodeDto ToDto(KnowledgeNode n) => new(
        n.Identity.Value, n.Kind.Value, n.Confidence.Value,
        n.Properties.ToDictionary(p => p.Key, p => p.Value), n.Evidence.Select(ToDto).ToList());

    private static RelationshipDto ToDto(Relationship r) => new(
        r.Type.Value, r.From.Value, r.To.Value, r.Confidence.Value, r.Evidence.Select(ToDto).ToList());

    private static EvidenceDto ToDto(Evidence e) => new(
        e.Repository.Value, e.Commit.Value, e.Engine, e.Method.ToString(), e.Confidence.Value,
        e.Location?.File, e.Location?.Line, e.Location?.Symbol);

    // ---- dto → domain (through the invariant-enforcing factories) ----
    private static KnowledgeNode ToDomain(NodeDto n) => KnowledgeNode.Create(
        KnowledgeIdentity.Parse(n.Identity), NodeKind.From(n.Kind),
        n.Evidence.Select(ToDomain).ToList(), Confidence.From(n.Confidence), n.Properties);

    private static Relationship ToDomain(RelationshipDto r) => Relationship.Create(
        RelationshipType.From(r.Type), KnowledgeIdentity.Parse(r.From), KnowledgeIdentity.Parse(r.To),
        r.Evidence.Select(ToDomain).ToList(), Confidence.From(r.Confidence));

    private static Evidence ToDomain(EvidenceDto e) => Evidence.Create(
        new RepositoryId(e.Repository), new Commit(e.Commit), e.Engine,
        Enum.Parse<ExtractionMethod>(e.Method), Confidence.From(e.Confidence),
        e.File is null ? null : SourceLocation.Create(e.File, e.Line, e.Symbol));
}
