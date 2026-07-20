namespace Aip.Core.Domain;

// Node kinds and relationship types are governed vocabulary (Session 2 §1–2, §7). The Core defines
// only the shape of a kind/type; the actual catalog is owned by the Schema Registry (the union of every
// loaded plugin's own declared Capabilities — see InMemorySchemaRegistry), so plugins extend the
// vocabulary without a Core change. Registry membership is advisory, not a hard gate: an unrecognized
// node kind is still accepted into Knowledge but surfaced as a validation warning (never silently
// dropped — see Diagnostic's own domain-value comment), since a stale plugin manifest shouldn't be able
// to destroy real facts. Relationship types are not currently checked against the registry, only node
// kinds. Plugin-contributed kinds are namespaced (e.g. "azure:resource"); core kinds are bare (e.g.
// "Endpoint").

/// <summary>
/// Node kinds that are always registered, independent of any loaded plugin — the Schema Registry unions
/// these into its catalog so validation never flags them as unrecognized (see InMemorySchemaRegistry).
/// Reserved for concepts that are intrinsic to the Core domain rather than owned by a specific technology
/// plugin.
/// </summary>
public static class CoreNodeKinds
{
    /// <summary>
    /// A referenced identity that lies outside the analyzed estate (a third-party library type, an
    /// external API, …). Synthesized by Validation so relationships to it are preserved instead of
    /// dropped — see ValidationPipeline.
    /// </summary>
    public const string External = "External";
}

/// <summary>The kind of a Knowledge Node (Endpoint, Entity, Type, Package, azure:resource, …).</summary>
public readonly record struct NodeKind
{
    public string Value { get; }

    public NodeKind(string value)
    {
        Value = Guard.NotNullOrWhiteSpace(value, nameof(NodeKind));
        Guard.Requires(!value.Contains('/'), "NodeKind must not contain '/'.");
    }

    public static NodeKind From(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// A relationship type from the controlled vocabulary (CONTAINS, CALLS, EXPOSES, MAPS_TO, …).
/// The Core validates form only; the legal set is governed by the Schema Registry.
/// </summary>
public readonly record struct RelationshipType
{
    public string Value { get; }

    public RelationshipType(string value)
    {
        Value = Guard.NotNullOrWhiteSpace(value, nameof(RelationshipType));
        Guard.Requires(!value.Contains('/'), "RelationshipType must not contain '/'.");
    }

    public static RelationshipType From(string value) => new(value);

    public override string ToString() => Value;
}
