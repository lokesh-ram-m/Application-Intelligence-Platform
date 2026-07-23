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
/// Exercises MigrationAnalyzer — EF Core migrations (classes inheriting Migration, with an Up() override)
/// summarized from the literal table/column names their own migrationBuilder calls pass.
/// </summary>
public class MigrationAnalyzerTests
{
    private const string EfFakeFramework = """
        namespace Microsoft.EntityFrameworkCore.Migrations
        {
            public class Migration
            {
                protected virtual void Up(MigrationBuilder migrationBuilder) { }
                protected virtual void Down(MigrationBuilder migrationBuilder) { }
            }
            public class MigrationBuilder
            {
                public void CreateTable(string name, object columns = null) { }
                public void DropTable(string name) { }
                public void AddColumn(string name, string table) { }
            }
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
        SyntaxTree tree = CSharpSyntaxTree.ParseText(EfFakeFramework + "\n" + source, path: Path.Combine(Path.GetTempPath(), "Test.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("TestAssembly", new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = new RoslynSemanticModel(compilation, new[] { tree });

        var sink = new FakeSink();
        await new MigrationAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Migration_summarizes_its_Up_method_from_the_literal_table_names()
    {
        List<NodeDiscovery> found = await Analyze("""
            namespace TestApp.Migrations
            {
                public partial class AddOrdersTable : Microsoft.EntityFrameworkCore.Migrations.Migration
                {
                    protected override void Up(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.CreateTable(name: "Orders");
                        migrationBuilder.AddColumn(name: "Total", table: "Orders");
                    }
                }
            }
            """);

        NodeDiscovery migration = Assert.Single(found, n => n.Kind.Value == "Migration");
        Assert.Equal("AddOrdersTable", migration.Properties["name"]);
        Assert.Equal("CreateTable(Orders); AddColumn(Total)", migration.Properties["operations"]);
    }

    [Fact]
    public async Task A_plain_class_named_similarly_to_a_migration_is_not_captured()
    {
        List<NodeDiscovery> found = await Analyze("""
            namespace TestApp
            {
                public class DataMigrationHelper
                {
                    public void Up() { }
                }
            }
            """);

        Assert.Empty(found);
    }
}
