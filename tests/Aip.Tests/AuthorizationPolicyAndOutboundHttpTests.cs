using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.Roslyn;
using Aip.Plugins.AspNetCore;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises AuthorizationPolicyAnalyzer (named policy DEFINITIONS — what a policy actually requires, not
/// just that its name was referenced somewhere) and OutboundHttpAnalyzer (backend-initiated HTTP calls,
/// gated on the receiver's resolved type being the real System.Net.Http.HttpClient).
/// </summary>
public class AuthorizationPolicyAndOutboundHttpTests
{
    private const string FakeFramework = """
        namespace System.Net.Http
        {
            public class HttpClient
            {
                public System.Threading.Tasks.Task<object> GetAsync(string url) => null;
                public System.Threading.Tasks.Task<object> PostAsync(string url, object content) => null;
            }
        }
        namespace Microsoft.AspNetCore.Authorization
        {
            public class AuthorizationOptions
            {
                public void AddPolicy(string name, System.Action<AuthorizationPolicyBuilder> configure) { }
            }
            public class AuthorizationPolicyBuilder
            {
                public AuthorizationPolicyBuilder RequireRole(params string[] roles) => this;
                public AuthorizationPolicyBuilder RequireClaim(string claimType) => this;
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

    private static async Task<List<NodeDiscovery>> Analyze(IAnalyzer analyzer, RoslynSemanticModel model)
    {
        var sink = new FakeSink();
        await analyzer.AnalyzeAsync(new FakeContext(model, "Api"), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task AddPolicy_captures_what_the_policy_actually_requires()
    {
        List<NodeDiscovery> found = await Analyze(new AuthorizationPolicyAnalyzer(), Compile("""
            namespace TestApp
            {
                public class Startup
                {
                    public void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
                    {
                        options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
                    }
                }
            }
            """));

        NodeDiscovery policy = Assert.Single(found);
        Assert.Equal("AdminOnly", policy.Properties["name"]);
        Assert.Equal("RequireRole(Admin)", policy.Properties["requirements"]);
    }

    [Fact]
    public async Task Outbound_GET_call_on_a_real_HttpClient_is_captured_with_its_URL()
    {
        List<NodeDiscovery> found = await Analyze(new OutboundHttpAnalyzer(), Compile("""
            namespace TestApp
            {
                public class OrdersClient
                {
                    private readonly System.Net.Http.HttpClient _http;
                    public OrdersClient(System.Net.Http.HttpClient http) { _http = http; }
                    public System.Threading.Tasks.Task<object> GetOrders() => _http.GetAsync("https://api.example.com/orders");
                }
            }
            """));

        NodeDiscovery call = Assert.Single(found);
        Assert.Equal("GET", call.Properties["verb"]);
        Assert.Equal("OrdersClient", call.Properties["owner"]);
        Assert.Equal("\"https://api.example.com/orders\"", call.Properties["url"]);
    }

    [Fact]
    public async Task A_lookalike_GetAsync_on_a_non_HttpClient_type_is_not_captured()
    {
        List<NodeDiscovery> found = await Analyze(new OutboundHttpAnalyzer(), Compile("""
            namespace TestApp
            {
                public class CacheClient
                {
                    public System.Threading.Tasks.Task<object> GetAsync(string key) => null;
                }
                public class OrdersService
                {
                    private readonly CacheClient _cache;
                    public OrdersService(CacheClient cache) { _cache = cache; }
                    public System.Threading.Tasks.Task<object> GetCached() => _cache.GetAsync("orders");
                }
            }
            """));

        Assert.Empty(found);
    }
}
