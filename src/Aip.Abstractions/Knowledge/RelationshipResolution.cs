using Aip.Core.Domain;

namespace Aip.Abstractions.Knowledge;

/// <summary>
/// A pluggable resolver that derives relationships no single analyzer could know (frontend↔backend,
/// publisher↔subscriber, service↔datastore). It emits RelationshipDiscoveries which pass through the
/// same validation gate as analyzer discoveries (Platform Architecture, Session 4 §3).
/// </summary>
public interface IRelationshipResolver
{
    string Name { get; }
    Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(
        IReadOnlyList<KnowledgeNode> nodes,
        CancellationToken ct = default);
}

/// <summary>
/// Hosts the relationship resolvers and runs them over the merged graph, where every node is
/// canonically identified — the platform's cross-repository intelligence.
/// </summary>
public interface IRelationshipResolutionEngine
{
    Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(
        IReadOnlyList<KnowledgeNode> nodes,
        CancellationToken ct = default);
}
