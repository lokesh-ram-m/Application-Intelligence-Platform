using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Core.Domain;
using Aip.Plugins.AspNetCore;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises PackageAnalyzer directly against a real, throwaway .csproj file on disk — it reads
/// context.Artifact.Path via File.Exists/XDocument.Load, not a Roslyn compilation, so no semantic model is
/// needed. Covers the two facts added this session: the PackageReference's own Version attribute, and
/// ProjectReference elements becoming Project REFERENCES Project edges.
/// </summary>
public class PackageAnalyzerTests
{
    private sealed class FakeSink : IDiscoverySink
    {
        public List<NodeDiscovery> Nodes { get; } = new();
        public List<RelationshipDiscovery> Relationships { get; } = new();

        public void Add(Discovery discovery)
        {
            if (discovery is NodeDiscovery n) Nodes.Add(n);
            else if (discovery is RelationshipDiscovery r) Relationships.Add(r);
        }

        public void Report(Core.Domain.Diagnostic diagnostic) { }
    }

    // PackageAnalyzer reads only context.Artifact.Path/Name — it never touches context.Model — so this
    // just needs to satisfy the interface, not model anything real.
    private sealed class DummyModel : ISemanticModel
    {
        public string Parser => "csproj";
    }

    private sealed class FakeContext : IAnalysisContext
    {
        public FakeContext(string csprojPath, string projectName) =>
            (Model, Artifact) = (new DummyModel(), new Artifact(new RepositoryId("r"), csprojPath, "dotnet-project", projectName));

        public ExecutionId ExecutionId => new(Guid.NewGuid());
        public ExecutionScope Scope => new(new ApplicationId("app"), new[] { new RepositoryId("r") }, Array.Empty<string>(), ExecutionMode.Local, null);
        public Artifact Artifact { get; }
        public RepositoryId Repository => new("r");
        public Commit Commit => new("c");
        public string Engine => "csproj";
        public ISemanticModel Model { get; }

        public KnowledgeIdentity NodeId(params IdentitySegment[] tail)
        {
            KnowledgeIdentity id = KnowledgeIdentity.ForApplication(new ApplicationId("app"));
            foreach (IdentitySegment seg in tail) id = id.Append(seg);
            return id;
        }

        public KnowledgeIdentity AppNodeId(params IdentitySegment[] tail) => NodeId(tail);

        public Evidence Evidence(string? file = null, int? line = null, string? symbol = null) =>
            Core.Domain.Evidence.Create(Repository, Commit, Engine, ExtractionMethod.Deterministic, Confidence.Full);
    }

    private static string WriteCsproj(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aip-test-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Package_version_is_captured_on_the_technology_node()
    {
        string csproj = WriteCsproj("""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="MediatR" Version="12.4.1" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var sink = new FakeSink();
            await new PackageAnalyzer().AnalyzeAsync(new FakeContext(csproj, "Api"), sink);

            NodeDiscovery tech = Assert.Single(sink.Nodes, n => n.Kind.Value == "Technology");
            Assert.Equal("12.4.1", tech.Properties["version"]);
        }
        finally { File.Delete(csproj); }
    }

    [Fact]
    public async Task Project_reference_becomes_a_REFERENCES_edge_between_project_nodes()
    {
        string csproj = WriteCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Api.Domain\Api.Domain.csproj" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var sink = new FakeSink();
            await new PackageAnalyzer().AnalyzeAsync(new FakeContext(csproj, "Api"), sink);

            RelationshipDiscovery reference = Assert.Single(sink.Relationships, r => r.Type.Value == "REFERENCES");
            Assert.Equal("Api", reference.From.ShortName);
            Assert.Equal("Api.Domain", reference.To.ShortName);
        }
        finally { File.Delete(csproj); }
    }
}
