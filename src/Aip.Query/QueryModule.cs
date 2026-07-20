using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Query;
using Aip.Core.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Query;

/// <summary>
/// The Query Platform over the latest snapshot: node/relationship lookup, graph traversal, impact
/// analysis (transitive reverse-closure), search, and snapshot listing. Reads the Knowledge Model only.
/// </summary>
internal sealed class QueryPlatform : IKnowledgeQueryService, IQueryPlatform
{
    private readonly IKnowledgeStore _store;

    public QueryPlatform(IKnowledgeStore store) => _store = store;

    public async Task<KnowledgeNode?> GetNodeAsync(ApplicationId application, KnowledgeIdentity identity, CancellationToken ct = default)
    {
        Snapshot? s = await _store.GetSnapshotAsync(application, ct);

        return s?.FindNode(identity);
    }

    public async Task<IReadOnlyList<Relationship>> GetRelationshipsAsync(ApplicationId application, KnowledgeIdentity from, CancellationToken ct = default)
    {
        Snapshot? s = await _store.GetSnapshotAsync(application, ct);

        return s is null ? Array.Empty<Relationship>() : s.Relationships.Where(r => r.From.Equals(from)).ToList();
    }

    public async Task<IReadOnlyList<GraphHop>> TraverseAsync(ApplicationId application, KnowledgeIdentity from, int depth = 1, CancellationToken ct = default)
    {
        Snapshot? s = await _store.GetSnapshotAsync(application, ct);
        if (s is null) return Array.Empty<GraphHop>();

        var kinds = s.Nodes.ToDictionary(n => n.Identity, n => n.Kind.Value);
        var visited = new HashSet<KnowledgeIdentity> { from };
        var frontier = new List<KnowledgeIdentity> { from };
        var hops = new List<GraphHop>();

        for (int d = 0; d < depth; d++)
        {
            var next = new List<KnowledgeIdentity>();
            foreach (KnowledgeIdentity node in frontier)
                foreach (Relationship r in s.Relationships.Where(r => r.From.Equals(node)))
                    if (visited.Add(r.To))
                    {
                        hops.Add(new GraphHop(r.Type, r.To, kinds.GetValueOrDefault(r.To, "?")));
                        next.Add(r.To);
                    }
            frontier = next;
        }

        return hops;
    }

    public async Task<IReadOnlyList<KnowledgeIdentity>> ImpactAsync(ApplicationId application, KnowledgeIdentity target, CancellationToken ct = default)
    {
        Snapshot? s = await _store.GetSnapshotAsync(application, ct);
        if (s is null) return Array.Empty<KnowledgeIdentity>();

        // Reverse transitive closure: everything that reaches the target.
        var impacted = new HashSet<KnowledgeIdentity>();
        var frontier = new Queue<KnowledgeIdentity>();
        frontier.Enqueue(target);
        while (frontier.Count > 0)
        {
            KnowledgeIdentity current = frontier.Dequeue();
            foreach (Relationship r in s.Relationships.Where(r => r.To.Equals(current)))
                if (impacted.Add(r.From))
                    frontier.Enqueue(r.From);
        }

        return impacted.ToList();
    }

    public async Task<IReadOnlyList<KnowledgeNode>> SearchAsync(ApplicationId application, string term, CancellationToken ct = default)
    {
        Snapshot? s = await _store.GetSnapshotAsync(application, ct);
        if (s is null) return Array.Empty<KnowledgeNode>();

        return s.Nodes.Where(n =>
            n.Identity.Value.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            n.Properties.Values.Any(v => v.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public async Task<IReadOnlyList<SnapshotId>> SnapshotsAsync(ApplicationId application, CancellationToken ct = default)
    {
        IReadOnlyList<Snapshot> history = await _store.GetHistoryAsync(application, ct);

        return history.Select(h => h.Id).ToList();
    }
}

public static class QueryModule
{
    public static IServiceCollection AddAipQuery(this IServiceCollection services)
    {
        services.AddSingleton<QueryPlatform>();
        services.AddSingleton<IKnowledgeQueryService>(sp => sp.GetRequiredService<QueryPlatform>());
        services.AddSingleton<IQueryPlatform>(sp => sp.GetRequiredService<QueryPlatform>());

        return services;
    }
}
