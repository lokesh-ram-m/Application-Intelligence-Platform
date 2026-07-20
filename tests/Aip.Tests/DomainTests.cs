using Aip.Core.Domain;

using Xunit;

namespace Aip.Tests;

public class DomainTests
{
    private static Evidence Ev() =>
        Evidence.Create(new RepositoryId("backend"), new Commit("abc123"), "roslyn", ExtractionMethod.Deterministic, Confidence.Full);

    [Fact]
    public void Identity_encodes_hierarchy_and_round_trips()
    {
        KnowledgeIdentity id = KnowledgeIdentity.ForApplication(new ApplicationId("ShopApp"))
            .Append(new IdentitySegment("repo", "backend"))
            .Append(new IdentitySegment("type", "ShopApi.CustomerController"));

        Assert.Equal("node://app:ShopApp/repo:backend/type:ShopApi.CustomerController", id.Value);
        Assert.Equal(id, KnowledgeIdentity.Parse(id.Value));
    }

    [Fact]
    public void Identity_percent_encodes_routes_with_slashes()
    {
        KnowledgeIdentity id = KnowledgeIdentity.ForApplication(new ApplicationId("A"))
            .Append(new IdentitySegment("endpoint", "GET /api/tasks/{id}"));
        Assert.Equal("GET /api/tasks/{id}", id.Segments[^1].Value);
    }

    [Fact]
    public void Node_requires_evidence()
    {
        Assert.Throws<DomainException>(() =>
            KnowledgeNode.Create(KnowledgeIdentity.Parse("node://app:A/type:X"), NodeKind.From("Type"),
                System.Array.Empty<Evidence>(), Confidence.Full));
    }

    [Fact]
    public void Snapshot_rejects_dangling_relationship()
    {
        var app = new ApplicationId("A");
        KnowledgeIdentity a = KnowledgeIdentity.Parse("node://app:A/type:A");
        KnowledgeIdentity b = KnowledgeIdentity.Parse("node://app:A/type:B");
        KnowledgeNode node = KnowledgeNode.Create(a, NodeKind.From("Type"), new[] { Ev() }, Confidence.Full);
        Relationship rel = Relationship.Create(RelationshipType.From("CALLS"), a, b, new[] { Ev() }, Confidence.Full);

        Assert.Throws<DomainException>(() =>
            Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, new[] { node }, new[] { rel }));
    }

    [Fact]
    public void Execution_lifecycle_is_guarded()
    {
        var ex = AnalysisExecution.Start(ExecutionId.New(), new ApplicationId("A"), ExecutionMode.Local, System.DateTimeOffset.UtcNow);
        ex.Complete(ExecutionOutcome.Success, SnapshotId.New(), ExecutionMetrics.Empty, System.DateTimeOffset.UtcNow);
        Assert.Equal(ExecutionState.Completed, ex.State);
        Assert.Throws<DomainException>(() => ex.Report(Diagnostic.Info("late", "x")));
    }

    [Fact]
    public void Confidence_is_range_checked()
    {
        Assert.Throws<DomainException>(() => new Confidence(1.5));
        Assert.Equal(1.0, Confidence.Full.Value);
    }
}
