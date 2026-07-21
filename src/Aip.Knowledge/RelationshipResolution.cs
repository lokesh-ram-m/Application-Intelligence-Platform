using Aip.Abstractions.Knowledge;
using Aip.Core.Domain;

namespace Aip.Knowledge;

internal static class ResolverHelp
{
    public static string? Segment(this KnowledgeNode node, string kind) =>
        node.Identity.Segments.FirstOrDefault(s => s.Kind == kind) is { } seg && seg.Kind == kind ? seg.Value : null;

    public static Evidence Derive(KnowledgeNode basis, double confidence)
    {
        Evidence e = basis.Evidence[0];

        return Aip.Core.Domain.Evidence.Create(
            e.Repository, e.Commit, "relationship-resolver", ExtractionMethod.Deterministic, new Confidence(confidence));
    }

    public static string NormalizeRoute(string route)
    {
        string r = route.Trim().ToLowerInvariant();
        int q = r.IndexOf('?');
        if (q >= 0) r = r[..q];
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\$\{[^}]*\}", "{}"); // ${id}
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\{[^}]*\}", "{}");     // {id}
        r = System.Text.RegularExpressions.Regex.Replace(r, @":[a-z0-9_]+", "{}");   // :id

        return "/" + r.Trim('/');
    }

    /// <summary>Exact verb + normalized-route match — shared by every resolver that maps an ApiCall to its Endpoint.</summary>
    public static KnowledgeNode? MatchEndpointExact(IEnumerable<KnowledgeNode> endpoints, string verb, string normalizedRoute) =>
        endpoints.FirstOrDefault(e =>
            string.Equals(e.Prop("verb"), verb, StringComparison.OrdinalIgnoreCase) &&
            NormalizeRoute(e.Prop("route") ?? "") == normalizedRoute);
}

/// <summary>
/// Maps frontend HTTP calls to the ASP.NET endpoints they target (frontend→backend). Matches purely by
/// NodeKind ("ApiCall"/"Endpoint") and the verb/url properties every frontend plugin emits them with
/// (Angular, React, Next.js all funnel through this same shape), so one resolver covers every frontend —
/// it isn't Angular-specific despite the class's origin.
/// </summary>
internal sealed class ApiCallToEndpointResolver : IRelationshipResolver
{
    public string Name => "apicall-endpoint";

    public Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        var endpoints = nodes.Where(n => n.Kind.Value == "Endpoint").ToList();
        var apiCalls = nodes.Where(n => n.Kind.Value == "ApiCall").ToList();
        var results = new List<RelationshipDiscovery>();

        foreach (KnowledgeNode call in apiCalls)
        {
            string verb = call.Prop("verb") ?? "";
            string norm = ResolverHelp.NormalizeRoute(call.Prop("url") ?? "");

            // Exact normalized match first; then a suffix match for calls whose base URL differs (e.g. the
            // frontend uses an env-var base, so "/clients" should still map to backend "/api/clients").
            KnowledgeNode? match = ResolverHelp.MatchEndpointExact(endpoints, verb, norm);
            double confidence = 0.9;
            if (match is null && norm.Length > 1)
            {
                // A suffix match against more than one endpoint is a coin-flip, not a resolution —
                // asserting 0.75 confidence for an arbitrarily-picked candidate would misrepresent a
                // genuinely ambiguous match as a confident one. Leave it unresolved instead.
                List<KnowledgeNode> candidates = endpoints.Where(e =>
                    string.Equals(e.Prop("verb"), verb, StringComparison.OrdinalIgnoreCase) &&
                    ResolverHelp.NormalizeRoute(e.Prop("route") ?? "").EndsWith(norm, StringComparison.Ordinal)).ToList();
                if (candidates.Count == 1)
                {
                    match = candidates[0];
                    confidence = 0.75;
                }
            }
            if (match is null) continue;

            results.Add(RelationshipDiscovery.Create(
                RelationshipType.From("MAPS_TO"), call.Identity, match.Identity,
                new[] { ResolverHelp.Derive(call, confidence) }, new Confidence(confidence)));
        }

        return Task.FromResult<IReadOnlyList<RelationshipDiscovery>>(results);
    }
}

/// <summary>Links services to the data stores in the same project scope (Service → Database).</summary>
internal sealed class ServiceToDatabaseResolver : IRelationshipResolver
{
    public string Name => "service-database";

    public Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        var services = nodes.Where(n => n.Kind.Value == "Service").ToList();
        var stores = nodes.Where(n => n.Kind.Value == "DataStore").ToList();
        var results = new List<RelationshipDiscovery>();

        foreach (KnowledgeNode store in stores)
        {
            string? project = store.Segment("project");
            if (project is null) continue;
            foreach (KnowledgeNode service in services.Where(s => s.Segment("project") == project))
            {
                results.Add(RelationshipDiscovery.Create(
                    RelationshipType.From("USES"), service.Identity, store.Identity,
                    new[] { ResolverHelp.Derive(service, 0.7) }, new Confidence(0.7)));
            }
        }

        return Task.FromResult<IReadOnlyList<RelationshipDiscovery>>(results);
    }
}

/// <summary>Links publishers to subscribers by topic/queue name (Publisher → Subscriber).</summary>
internal sealed class PublisherSubscriberResolver : IRelationshipResolver
{
    public string Name => "publisher-subscriber";

    public Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        var publishers = nodes.Where(n => n.Prop("role") == "publisher").ToList();
        var subscribers = nodes.Where(n => n.Prop("role") == "subscriber").ToList();
        var results = new List<RelationshipDiscovery>();

        foreach (KnowledgeNode pub in publishers)
            foreach (KnowledgeNode sub in subscribers.Where(s => s.Prop("topic") == pub.Prop("topic") && s.Prop("topic") is not null))
                results.Add(RelationshipDiscovery.Create(
                    RelationshipType.From("PUBLISHES"), pub.Identity, sub.Identity,
                    new[] { ResolverHelp.Derive(pub, 0.8) }, new Confidence(0.8)));

        return Task.FromResult<IReadOnlyList<RelationshipDiscovery>>(results);
    }
}

/// <summary>
/// Links an audit-log fact (see AuditLogAnalyzer, Aip.Plugins.AspNetCore) to the actual Entity node it
/// names, by exact (case-insensitive) match against the Entity's own "name" property — the same kind of
/// string-to-node matching ApiCallToEndpointResolver already does for verb/route, just for a different pair
/// of Kinds. An audit fact with no matching Entity (e.g. a typo, or the entity type genuinely isn't
/// analyzed in this run) is simply left unresolved rather than guessed at.
/// </summary>
internal sealed class AuditLogToEntityResolver : IRelationshipResolver
{
    public string Name => "auditlog-entity";

    public Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        var entities = nodes.Where(n => n.Kind.Value == "Entity").ToList();
        var auditLogs = nodes.Where(n => n.Kind.Value == "AuditLog").ToList();
        var results = new List<RelationshipDiscovery>();

        foreach (KnowledgeNode audit in auditLogs)
        {
            string? entityType = audit.Prop("entityType");
            if (entityType is null) continue;
            KnowledgeNode? match = entities.FirstOrDefault(e => string.Equals(e.Prop("name"), entityType, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;

            results.Add(RelationshipDiscovery.Create(
                RelationshipType.From("AUDITS"), audit.Identity, match.Identity,
                new[] { ResolverHelp.Derive(audit, 0.7) }, new Confidence(0.7)));
        }

        return Task.FromResult<IReadOnlyList<RelationshipDiscovery>>(results);
    }
}

/// <summary>
/// Elevates cross-repository call mappings to repository-level dependencies (Cross-repo dependency):
/// when a frontend ApiCall maps to a backend Endpoint, the calling repo DEPENDS_ON the serving repo.
/// </summary>
internal sealed class CrossRepositoryDependencyResolver : IRelationshipResolver
{
    public string Name => "cross-repository";

    public Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        var endpoints = nodes.Where(n => n.Kind.Value == "Endpoint").ToList();
        var apiCalls = nodes.Where(n => n.Kind.Value == "ApiCall").ToList();
        var results = new List<RelationshipDiscovery>();
        var seen = new HashSet<(string, string)>();

        foreach (KnowledgeNode call in apiCalls)
        {
            string? callerRepo = call.Segment("repo");
            if (callerRepo is null) continue;
            string verb = call.Prop("verb") ?? "";
            string norm = ResolverHelp.NormalizeRoute(call.Prop("url") ?? "");
            KnowledgeNode? match = ResolverHelp.MatchEndpointExact(endpoints, verb, norm);
            string? serverRepo = match?.Segment("repo");
            if (match is null || serverRepo is null || serverRepo == callerRepo) continue;
            if (!seen.Add((callerRepo, serverRepo))) continue;

            KnowledgeIdentity callerNode = KnowledgeIdentity.ForApplication(new ApplicationId(App(call)))
                .Append(new IdentitySegment("repo", callerRepo));
            KnowledgeIdentity serverNode = KnowledgeIdentity.ForApplication(new ApplicationId(App(match)))
                .Append(new IdentitySegment("repo", serverRepo));
            results.Add(RelationshipDiscovery.Create(
                RelationshipType.From("DEPENDS_ON"), callerNode, serverNode,
                new[] { ResolverHelp.Derive(call, 0.85) }, new Confidence(0.85)));
        }

        return Task.FromResult<IReadOnlyList<RelationshipDiscovery>>(results);
    }

    private static string App(KnowledgeNode node) =>
        node.Identity.Segments.FirstOrDefault(s => s.Kind == "app").Value is { Length: > 0 } v ? v : "app";
}

/// <summary>
/// Hosts the relationship resolvers and runs them over the merged, canonically-identified graph — the
/// platform's cross-repository intelligence. Emits RelationshipDiscoveries that go through Validation.
/// </summary>
internal sealed class RelationshipResolutionEngine : IRelationshipResolutionEngine
{
    private readonly IReadOnlyList<IRelationshipResolver> _resolvers;

    public RelationshipResolutionEngine(IEnumerable<IRelationshipResolver> resolvers) => _resolvers = resolvers.ToList();

    public async Task<IReadOnlyList<RelationshipDiscovery>> ResolveAsync(IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        var all = new List<RelationshipDiscovery>();
        foreach (IRelationshipResolver resolver in _resolvers)
        {
            ct.ThrowIfCancellationRequested();
            all.AddRange(await resolver.ResolveAsync(nodes, ct));
        }

        return all;
    }
}
