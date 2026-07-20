using Aip.Abstractions.Registries;
using Aip.Abstractions.Validation;
using Aip.Core.Domain;

namespace Aip.Knowledge;

/// <summary>
/// The single writer's gate. Normalizes identity, de-duplicates and merges node discoveries, aggregates
/// evidence, computes corroborated confidence, resolves kind conflicts, checks kinds against the Schema
/// Registry, and validates relationship endpoints against the accepted node set. Only this pipeline turns
/// Discoveries into Knowledge.
/// </summary>
internal sealed class ValidationPipeline : IValidationPipeline
{
    private readonly ISchemaRegistry _schema;

    public ValidationPipeline(ISchemaRegistry schema) => _schema = schema;

    public async Task<ValidationResult> ValidateAsync(
        IReadOnlyList<Discovery> discoveries,
        IReadOnlyCollection<KnowledgeIdentity>? knownNodeIdentities = null,
        CancellationToken ct = default)
    {
        var nodeDiscoveries = discoveries.OfType<NodeDiscovery>().ToList();
        var relDiscoveries = discoveries.OfType<RelationshipDiscovery>().ToList();
        var diagnostics = new List<Diagnostic>();

        // Fetched once per validation pass, not per node — the catalog is the union of every loaded
        // plugin's declared Capabilities, so it doesn't change within a single run.
        var registeredKinds = new HashSet<string>((await _schema.GetNodeKindsAsync(ct)).Select(k => k.Kind));

        // --- Identity normalization + duplicate detection/merge ---
        var canonicalByKey = new Dictionary<string, KnowledgeIdentity>();
        var nodes = new List<KnowledgeNode>();

        foreach (IGrouping<string, NodeDiscovery> group in nodeDiscoveries.GroupBy(KeyOf))
        {
            List<NodeDiscovery> members = group.ToList();

            // Schema validation: kind must be well-formed (the value object enforces it) AND registered
            // (declared in some loaded plugin's Capabilities). An unregistered kind is surfaced, never
            // silently dropped — honesty about what wasn't understood is a domain value (see Diagnostic).
            NodeDiscovery canonical = members
                .OrderByDescending(d => d.Identity.Segments.Count)
                .ThenByDescending(d => d.Identity.Value.Length)
                .First();

            // Conflict detection/resolution: differing kinds → keep the highest-confidence kind.
            var kinds = members.Select(m => m.Kind.Value).Distinct().ToList();
            NodeKind kind = members.OrderByDescending(m => m.Confidence.Value).First().Kind;
            if (!registeredKinds.Contains(kind.Value))
                diagnostics.Add(Diagnostic.Warning(
                    $"Unregistered node kind '{kind.Value}' for {canonical.Identity.ShortName} — no loaded plugin declares it in its manifest Capabilities.", "validation"));

            // Evidence aggregation + confidence corroboration.
            var evidence = members.SelectMany(m => m.Evidence).ToList();
            var properties = MergeProperties(members);
            double confidence = Corroborate(members.Select(m => m.Confidence.Value));

            // A losing kind is still a real, evidenced classification — recording it only in a transient
            // Warning would erase it from the graph itself the moment logs roll over. Carry it forward as
            // a node property so anything reading the graph later (docs, viewer, future conflict-review
            // tooling) can still see the identity was ambiguous, not just the winning guess.
            if (kinds.Count > 1)
            {
                properties["AlternateKinds"] = string.Join(",", kinds.Where(k => k != kind.Value));
                diagnostics.Add(Diagnostic.Warning(
                    $"Kind conflict for {canonical.Identity.ShortName}: [{string.Join(", ", kinds)}] → chose '{kind.Value}' (alternates kept in 'AlternateKinds' property).", "validation"));
            }

            nodes.Add(KnowledgeNode.Create(canonical.Identity, kind, evidence, new Confidence(confidence), properties));
            canonicalByKey[group.Key] = canonical.Identity;
        }

        // --- Relationship validation (endpoints must resolve to accepted, known, or external nodes) ---
        var known = new HashSet<KnowledgeIdentity>(nodes.Select(n => n.Identity));
        if (knownNodeIdentities is not null)
            foreach (KnowledgeIdentity id in knownNodeIdentities) known.Add(id);

        KnowledgeIdentity Canon(KnowledgeIdentity id) =>
            canonicalByKey.TryGetValue(KeyOf(id), out KnowledgeIdentity c) ? c : id;

        // An endpoint outside the analyzed estate (a library type, a third-party API) is still a real,
        // evidenced fact — rather than dropping the relationship, the missing endpoint is grounded as a
        // synthetic External node the first time it's seen (evidence from that first reference; later
        // references to the same identity just reuse it, matching how Core kinds don't require the full
        // corroboration pass real analyzer-discovered nodes go through). A relationship where NEITHER
        // endpoint is known isn't attributable to anything actually discovered, so that case is still
        // rejected outright.
        void Externalize(KnowledgeIdentity id, RelationshipDiscovery rel)
        {
            nodes.Add(KnowledgeNode.Create(id, NodeKind.From(CoreNodeKinds.External), rel.Evidence, rel.Confidence));
            known.Add(id);
        }

        var relationships = new Dictionary<(string, string, string), List<RelationshipDiscovery>>();
        foreach (RelationshipDiscovery rel in relDiscoveries)
        {
            KnowledgeIdentity from = Canon(rel.From);
            KnowledgeIdentity to = Canon(rel.To);
            if (from.Equals(to)) continue;

            bool fromKnown = known.Contains(from);
            bool toKnown = known.Contains(to);
            if (!fromKnown && !toKnown)
            {
                diagnostics.Add(Diagnostic.Debug(
                    $"Rejected {rel.Type.Value} {rel.From.ShortName}→{rel.To.ShortName}: neither endpoint is in Knowledge.", "validation"));
                continue;
            }
            if (!fromKnown) Externalize(from, rel);
            if (!toKnown) Externalize(to, rel);

            var key = (rel.Type.Value, from.Value, to.Value);
            if (!relationships.TryGetValue(key, out List<RelationshipDiscovery>? bucket))
                relationships[key] = bucket = new List<RelationshipDiscovery>();
            bucket.Add(rel with { });
        }

        var acceptedRelationships = new List<Relationship>();
        foreach (((string type, string fromVal, string toVal), List<RelationshipDiscovery> bucket) in relationships.Select(kv => (kv.Key, kv.Value)))
        {
            var evidence = bucket.SelectMany(b => b.Evidence).ToList();
            double confidence = Corroborate(bucket.Select(b => b.Confidence.Value));
            RelationshipDiscovery any = bucket[0];
            acceptedRelationships.Add(Relationship.Create(
                RelationshipType.From(type), Canon(any.From), Canon(any.To), evidence, new Confidence(confidence)));
        }

        return new ValidationResult(nodes, acceptedRelationships, diagnostics);
    }

    /// <summary>
    /// Normalization key: the exact canonical identity. Analyzers now emit semantically-resolved,
    /// fully-qualified identities (namespace + nested types + generic arity + project scope), so exact
    /// identity is the safe deduplication key — no lossy simple-name collapse that could merge distinct
    /// types sharing a short name across namespaces.
    /// </summary>
    private static string KeyOf(NodeDiscovery d) => KeyOf(d.Identity);

    private static string KeyOf(KnowledgeIdentity id) => id.Value;

    private static Dictionary<string, string> MergeProperties(IEnumerable<NodeDiscovery> members)
    {
        var props = new Dictionary<string, string>();
        foreach (NodeDiscovery m in members.OrderBy(m => m.Confidence.Value))
            foreach (KeyValuePair<string, string> p in m.Properties)
                props[p.Key] = p.Value;

        return props;
    }

    private static double Corroborate(IEnumerable<double> confidences)
    {
        double complement = 1.0;
        foreach (double c in confidences) complement *= (1.0 - Math.Clamp(c, 0.0, 1.0));

        return Math.Clamp(1.0 - complement, 0.0, 1.0);
    }
}
