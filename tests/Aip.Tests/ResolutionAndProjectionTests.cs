using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Projections;
using Aip.Ai;
using Aip.Core.Domain;
using Aip.Infrastructure;
using Aip.Knowledge;
using Aip.Projections;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aip.Tests;

internal static class Ids
{
    // Build identities through the builder so segment values (e.g. routes with '/') are encoded correctly.
    public static KnowledgeIdentity Of(string app, params (string Kind, string Value)[] segments)
    {
        KnowledgeIdentity id = KnowledgeIdentity.ForApplication(new ApplicationId(app));
        foreach ((string kind, string value) in segments) id = id.Append(new IdentitySegment(kind, value));

        return id;
    }
}

public class ResolutionTests
{
    private static Evidence Ev() => Evidence.Create(new RepositoryId("r"), new Commit("c"), "t", ExtractionMethod.Deterministic, Confidence.Full);

    private static KnowledgeNode Node(KnowledgeIdentity id, string kind, params (string, string)[] props)
    {
        var d = new Dictionary<string, string>();
        foreach ((string k, string v) in props) d[k] = v;

        return KnowledgeNode.Create(id, NodeKind.From(kind), new[] { Ev() }, Confidence.Full, d);
    }

    private static IRelationshipResolutionEngine Engine() =>
        new ServiceCollection().AddAipKnowledge().BuildServiceProvider().GetRequiredService<IRelationshipResolutionEngine>();

    [Fact]
    public async Task Angular_call_maps_to_backend_endpoint()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("endpoint", "GET /api/Customer")), "Endpoint", ("verb", "GET"), ("route", "/api/Customer")),
            Node(Ids.Of("A", ("repo", "web"), ("apicall", "GET /api/customer")), "ApiCall", ("verb", "GET"), ("url", "/api/customer")),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        Assert.Contains(resolved, r => r.Type.Value == "MAPS_TO");
    }

    [Fact]
    public async Task Suffix_match_resolves_when_the_candidate_endpoint_is_unique()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("endpoint", "GET /api/v1/clients")), "Endpoint", ("verb", "GET"), ("route", "/api/v1/clients")),
            Node(Ids.Of("A", ("repo", "web"), ("apicall", "GET /clients")), "ApiCall", ("verb", "GET"), ("url", "/clients")),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        Assert.Contains(resolved, r => r.Type.Value == "MAPS_TO");
    }

    [Fact]
    public async Task Suffix_match_is_skipped_when_multiple_endpoints_could_match()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("endpoint", "GET /api/v1/clients")), "Endpoint", ("verb", "GET"), ("route", "/api/v1/clients")),
            Node(Ids.Of("A", ("endpoint", "GET /api/v2/clients")), "Endpoint", ("verb", "GET"), ("route", "/api/v2/clients")),
            Node(Ids.Of("A", ("repo", "web"), ("apicall", "GET /clients")), "ApiCall", ("verb", "GET"), ("url", "/clients")),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        // Two endpoints share the suffix "/clients" — picking either one would be a coin-flip, so the
        // call is left unresolved rather than asserting a confident-looking false positive.
        Assert.DoesNotContain(resolved, r => r.Type.Value == "MAPS_TO");
    }

    [Fact]
    public async Task Service_and_datastore_in_same_project_are_linked()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("repo", "b"), ("project", "Api"), ("type", "Ns.CustomerService")), "Service"),
            Node(Ids.Of("A", ("repo", "b"), ("project", "Api"), ("datastore", "ShopDbContext")), "DataStore"),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        Assert.Contains(resolved, r => r.Type.Value == "USES");
    }
}

public class ProjectionTests
{
    private static IProjectionEngine Engine()
    {
        var services = new ServiceCollection();
        services.AddAipInfrastructure(new ConfigurationBuilder().Build());
        services.AddAipAi();
        services.AddAipProjections();

        return services.BuildServiceProvider().GetRequiredService<IProjectionEngine>();
    }

    private static Evidence Ev() => Evidence.Create(new RepositoryId("r"), new Commit("c"), "t", ExtractionMethod.Deterministic, Confidence.Full);

    [Fact]
    public async Task Documentation_is_generated_from_the_snapshot_only()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity controller = Ids.Of("ShopApp", ("repo", "b"), ("type", "Api.CustomerController"));
        KnowledgeIdentity endpoint = Ids.Of("ShopApp", ("endpoint", "GET /api/Customer"));

        var nodes = new[]
        {
            KnowledgeNode.Create(controller, NodeKind.From("Controller"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CustomerController" }),
            KnowledgeNode.Create(endpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["route"] = "/api/Customer" }),
        };
        var rels = new[] { Relationship.Create(RelationshipType.From("EXPOSES"), controller, endpoint, new[] { Ev() }, Confidence.Full) };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        var artifacts = results.SelectMany(r => r.Artifacts).ToList();

        Assert.Contains(artifacts, a => a.Name == "product-specification/overview.md");
        Assert.Contains(artifacts, a => a.Name == "technical-specification/api-reference.md" && a.Content.Contains("/api/Customer"));
        Assert.Contains(artifacts, a => a.Name == "technical-specification/architecture.md" && a.Content.Contains("EXPOSES"));
    }
}
