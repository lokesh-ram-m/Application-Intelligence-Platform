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
/// Exercises ConfigurationUsageAnalyzer (IConfiguration/environment-variable/feature-flag reads) and
/// InfrastructureAnalyzer's timer detection against a real, in-memory Roslyn compilation. No real
/// Microsoft.Extensions.Configuration/FeatureManagement package is referenced — detection matches by
/// interface/type name, so a minimal in-source fake is enough to exercise the real analyzer code.
/// </summary>
public class ConfigurationUsageAnalyzerTests
{
    private const string FakeFramework = """
        namespace Microsoft.Extensions.Configuration
        {
            public interface IConfiguration
            {
                string this[string key] { get; }
                IConfiguration GetSection(string key);
            }
        }
        namespace Microsoft.FeatureManagement
        {
            public interface IFeatureManager
            {
                System.Threading.Tasks.Task<bool> IsEnabledAsync(string feature);
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

    private static RoslynSemanticModel Compile(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(FakeFramework + "\n" + source, path: Path.Combine(Path.GetTempPath(), "Test.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("TestAssembly", new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new RoslynSemanticModel(compilation, new[] { tree });
    }

    [Fact]
    public async Task IConfiguration_indexer_with_a_literal_key_is_captured()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public class Startup
                {
                    public string Read(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
                        configuration["Smtp:Host"];
                }
            }
            """);

        var sink = new FakeSink();
        await new ConfigurationUsageAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery cfg = Assert.Single(sink.Nodes);
        Assert.Equal("Smtp:Host", cfg.Properties["name"]);
        Assert.Equal("IConfiguration", cfg.Properties["source"]);
    }

    [Fact]
    public async Task Environment_variable_read_is_captured()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public class Startup
                {
                    public string Read() => System.Environment.GetEnvironmentVariable("AIP_SQL_CONNECTION_STRING");
                }
            }
            """);

        var sink = new FakeSink();
        await new ConfigurationUsageAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery cfg = Assert.Single(sink.Nodes);
        Assert.Equal("AIP_SQL_CONNECTION_STRING", cfg.Properties["name"]);
        Assert.Equal("EnvironmentVariable", cfg.Properties["source"]);
    }

    [Fact]
    public async Task Feature_flag_check_is_captured()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public class Startup
                {
                    public System.Threading.Tasks.Task<bool> Read(Microsoft.FeatureManagement.IFeatureManager fm) =>
                        fm.IsEnabledAsync("NewCheckoutFlow");
                }
            }
            """);

        var sink = new FakeSink();
        await new ConfigurationUsageAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery cfg = Assert.Single(sink.Nodes);
        Assert.Equal("NewCheckoutFlow", cfg.Properties["name"]);
        Assert.Equal("FeatureFlag", cfg.Properties["source"]);
    }

    [Fact]
    public async Task A_key_built_from_a_variable_is_not_guessed_at()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public class Startup
                {
                    public string Read(Microsoft.Extensions.Configuration.IConfiguration configuration, string key) =>
                        configuration[key];
                }
            }
            """);

        var sink = new FakeSink();
        await new ConfigurationUsageAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        Assert.Empty(sink.Nodes);
    }

    [Fact]
    public async Task Timer_construction_is_flagged_as_a_background_job()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public class Poller
                {
                    private readonly System.Threading.Timer _timer = new System.Threading.Timer(_ => { }, null, 0, 1000);
                }
            }
            """);

        var sink = new FakeSink();
        await new InfrastructureAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery job = Assert.Single(sink.Nodes, n => n.Kind.Value == "BackgroundJob");
        Assert.Equal("timer", job.Properties["detail"]);
        Assert.Contains("Poller", job.Properties["name"]);
    }
}
