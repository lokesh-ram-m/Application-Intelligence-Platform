using Aip.Abstractions.Registries;
using Aip.Abstractions.Validation;
using Aip.Core.Domain;
using Aip.Knowledge;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aip.Tests;

public class ValidationTests
{
    // These tests exercise merge/dedup/relationship logic, not Schema Registry enforcement — a permissive
    // fake keeps them isolated from the real plugin-manifest-backed registry (Aip.Registries).
    private sealed class AlwaysRegisteredSchemaRegistry : ISchemaRegistry
    {
        public Task<IReadOnlyList<NodeKindDefinition>> GetNodeKindsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NodeKindDefinition>>(Array.Empty<NodeKindDefinition>());
        public Task<bool> IsRegisteredAsync(string kind, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static IValidationPipeline Pipeline()
    {
        var services = new ServiceCollection();
        services.AddAipKnowledge();
        services.AddSingleton<ISchemaRegistry, AlwaysRegisteredSchemaRegistry>();

        return services.BuildServiceProvider().GetRequiredService<IValidationPipeline>();
    }

    private static Evidence Ev(string engine = "roslyn") =>
        Evidence.Create(new RepositoryId("r"), new Commit("c"), engine, ExtractionMethod.Deterministic, new Confidence(0.5));

    private static NodeDiscovery Node(string id, string kind, params Evidence[] ev) =>
        NodeDiscovery.Create(KnowledgeIdentity.Parse(id), NodeKind.From(kind), ev, new Confidence(0.5));

    [Fact]
    public async Task Duplicate_identities_merge_and_aggregate_evidence()
    {
        IValidationPipeline pipeline = Pipeline();
        var discoveries = new List<Discovery>
        {
            Node("node://app:A/repo:b/type:Ns.CustomerService", "Service", Ev("di")),
            Node("node://app:A/repo:b/type:Ns.CustomerService", "Service", Ev("roslyn")),
        };

        ValidationResult result = await pipeline.ValidateAsync(discoveries);

        Assert.Single(result.Nodes);
        Assert.Equal(2, result.Nodes[0].Evidence.Count);           // evidence aggregated
        Assert.Equal(0.75, result.Nodes[0].Confidence.Value, 3);   // corroboration 1-(1-.5)(1-.5)
    }

    [Fact]
    public async Task Kind_conflict_keeps_the_losing_kind_as_a_node_property()
    {
        IValidationPipeline pipeline = Pipeline();
        var discoveries = new List<Discovery>
        {
            Node("node://app:A/repo:b/type:Ns.Order", "Entity", Ev()),
            Node("node://app:A/repo:b/type:Ns.Order", "Event", Ev()),
        };

        ValidationResult result = await pipeline.ValidateAsync(discoveries);

        Assert.Single(result.Nodes);
        KnowledgeNode node = result.Nodes[0];
        Assert.Equal("Entity", node.Kind.Value); // equal confidence → stable tie-break keeps the first
        Assert.True(node.Properties.TryGetValue("AlternateKinds", out string? alternates));
        Assert.Equal("Event", alternates);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Kind conflict"));
    }

    [Fact]
    public async Task Distinct_identities_are_not_merged()
    {
        IValidationPipeline pipeline = Pipeline();
        var discoveries = new List<Discovery>
        {
            Node("node://app:A/repo:b/type:Ns1.Customer", "Entity", Ev()),
            Node("node://app:A/repo:b/type:Ns2.Customer", "Entity", Ev()),   // same short name, different namespace
        };

        ValidationResult result = await pipeline.ValidateAsync(discoveries);

        Assert.Equal(2, result.Nodes.Count); // must NOT collapse across namespaces
    }

    [Fact]
    public async Task Relationship_to_unresolved_endpoint_is_preserved_via_external_node()
    {
        IValidationPipeline pipeline = Pipeline();
        var discoveries = new List<Discovery>
        {
            Node("node://app:A/repo:b/type:X", "Service", Ev()),
            RelationshipDiscovery.Create(RelationshipType.From("CALLS"),
                KnowledgeIdentity.Parse("node://app:A/repo:b/type:X"),
                KnowledgeIdentity.Parse("node://app:A/repo:b/type:MISSING"),
                new[] { Ev() }, new Confidence(0.9)),
        };

        ValidationResult result = await pipeline.ValidateAsync(discoveries);

        Assert.Equal(2, result.Nodes.Count); // X plus a synthesized External node for MISSING
        Assert.Single(result.Relationships);
        Assert.Contains(result.Nodes, n => n.Kind.Value == CoreNodeKinds.External
            && n.Identity.Value == "node://app:A/repo:b/type:MISSING");
    }

    [Fact]
    public async Task Relationship_with_no_known_endpoint_is_rejected()
    {
        IValidationPipeline pipeline = Pipeline();
        var discoveries = new List<Discovery>
        {
            RelationshipDiscovery.Create(RelationshipType.From("CALLS"),
                KnowledgeIdentity.Parse("node://app:A/repo:b/type:MISSING1"),
                KnowledgeIdentity.Parse("node://app:A/repo:b/type:MISSING2"),
                new[] { Ev() }, new Confidence(0.9)),
        };

        ValidationResult result = await pipeline.ValidateAsync(discoveries);

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Relationships);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("neither endpoint is in Knowledge"));
    }
}
