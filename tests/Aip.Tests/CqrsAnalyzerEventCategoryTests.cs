using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Core.Domain;
using Aip.Engines.Roslyn;
using Aip.Plugins.AspNetCore;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises CqrsAnalyzer's Domain-vs-Integration event classification — namespace/folder text first, then
/// name suffix, same signal-priority order as the pre-existing Command-vs-Query classifier. An event with
/// neither signal is left uncategorized rather than guessed at.
/// </summary>
public class CqrsAnalyzerEventCategoryTests
{
    private const string FakeFramework = """
        namespace MediatR
        {
            public interface INotification { }
        }
        """;

    private sealed class FakeSink : IDiscoverySink
    {
        public List<NodeDiscovery> Nodes { get; } = new();
        public void Add(Discovery discovery) { if (discovery is NodeDiscovery n) Nodes.Add(n); }
        public void Report(Core.Domain.Diagnostic diagnostic) { }
    }

    private sealed class FakeContext : IAnalysisContext
    {
        public FakeContext(RoslynSemanticModel model, string projectName) =>
            (Model, Artifact) = (model, new Artifact(new RepositoryId("r"), "x", "dotnet-project", projectName));

        public ExecutionId ExecutionId => new(Guid.NewGuid());
        public ExecutionScope Scope => new(new ApplicationId("app"), new[] { new RepositoryId("r") }, Array.Empty<string>(), ExecutionMode.Local, null);
        public Artifact Artifact { get; }
        public RepositoryId Repository => new("r");
        public Commit Commit => new("c");
        public string Engine => "roslyn";
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

    private static readonly MetadataReference[] References = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
        .ToArray();

    private static async Task<List<NodeDiscovery>> Analyze(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(FakeFramework + "\n" + source, path: Path.Combine(Path.GetTempPath(), "Test.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("TestAssembly", new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = new RoslynSemanticModel(compilation, new[] { tree });

        var sink = new FakeSink();
        await new CqrsAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Event_in_a_Domain_namespace_is_categorized_Domain()
    {
        List<NodeDiscovery> found = await Analyze("""
            namespace TestApp.Domain.Events
            {
                public class OrderCreated : MediatR.INotification { }
            }
            """);

        NodeDiscovery ev = Assert.Single(found, n => n.Kind.Value == "Event");
        Assert.Equal("Domain", ev.Properties["category"]);
    }

    [Fact]
    public async Task Event_in_an_Integration_namespace_is_categorized_Integration()
    {
        List<NodeDiscovery> found = await Analyze("""
            namespace TestApp.IntegrationEvents
            {
                public class OrderShipped : MediatR.INotification { }
            }
            """);

        NodeDiscovery ev = Assert.Single(found, n => n.Kind.Value == "Event");
        Assert.Equal("Integration", ev.Properties["category"]);
    }

    [Fact]
    public async Task Event_with_neither_signal_is_left_uncategorized()
    {
        List<NodeDiscovery> found = await Analyze("""
            namespace TestApp.Notifications
            {
                public class SomethingHappened : MediatR.INotification { }
            }
            """);

        NodeDiscovery ev = Assert.Single(found, n => n.Kind.Value == "Event");
        Assert.False(ev.Properties.ContainsKey("category"));
    }
}
