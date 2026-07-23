using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.Angular;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises AngularComponentAnalyzer's standalone-component flag and AngularGuardAnalyzer's resolver
/// detection (class-based and functional) — both added this session to close capability-audit gaps.
/// </summary>
public class AngularAnalyzerTests
{
    private sealed class FakeSink : IDiscoverySink
    {
        public List<NodeDiscovery> Nodes { get; } = new();
        public void Add(Discovery discovery) { if (discovery is NodeDiscovery n) Nodes.Add(n); }
        public void Report(Diagnostic diagnostic) { }
    }

    private sealed class FakeContext : IAnalysisContext
    {
        public FakeContext(params TsFile[] files) => Model = new TypeScriptSemanticModel("heuristic", files);
        public ExecutionId ExecutionId => ExecutionId.New();
        public ExecutionScope Scope => new(new ApplicationId("app"), new[] { new RepositoryId("r") }, Array.Empty<string>(), ExecutionMode.Local, null);
        public Artifact Artifact => new(new RepositoryId("r"), "x", "angular-workspace", "x");
        public RepositoryId Repository => new("r");
        public Commit Commit => new("c");
        public string Engine => "heuristic";
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

    private static async Task<List<NodeDiscovery>> Analyze(IAnalyzer analyzer, params TsFile[] files)
    {
        var sink = new FakeSink();
        await analyzer.AnalyzeAsync(new FakeContext(files), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Standalone_component_is_flagged()
    {
        List<NodeDiscovery> found = await Analyze(new AngularComponentAnalyzer(), new TsFile("Widget.ts", """
            @Component({
              selector: 'app-widget',
              standalone: true,
              templateUrl: './widget.component.html',
            })
            export class WidgetComponent {}
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("true", n.Properties["standalone"]);
    }

    [Fact]
    public async Task Module_declared_component_is_not_flagged_standalone()
    {
        List<NodeDiscovery> found = await Analyze(new AngularComponentAnalyzer(), new TsFile("Widget.ts", """
            @Component({
              selector: 'app-widget',
              templateUrl: './widget.component.html',
            })
            export class WidgetComponent {}
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("standalone"));
    }

    [Fact]
    public async Task Class_based_resolver_is_detected()
    {
        List<NodeDiscovery> found = await Analyze(new AngularGuardAnalyzer(), new TsFile("order.resolver.ts", """
            export class OrderResolver implements Resolve<Order> {
              resolve(route: ActivatedRouteSnapshot): Observable<Order> { return this.orders.getOne(route.params['id']); }
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("Resolver", n.Kind.Value);
        Assert.Equal("OrderResolver", n.Properties["name"]);
    }

    [Fact]
    public async Task Functional_resolver_is_detected()
    {
        List<NodeDiscovery> found = await Analyze(new AngularGuardAnalyzer(), new TsFile("order.resolver.ts", """
            export const orderResolver: ResolveFn<Order> = (route) => inject(OrderService).getOne(route.params['id']);
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("Resolver", n.Kind.Value);
        Assert.Equal("orderResolver", n.Properties["name"]);
    }

    [Fact]
    public async Task Class_based_guard_is_still_detected_as_a_guard_not_a_resolver()
    {
        List<NodeDiscovery> found = await Analyze(new AngularGuardAnalyzer(), new TsFile("auth.guard.ts", """
            export class AuthGuard implements CanActivate {
              canActivate(): boolean { return true; }
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("Guard", n.Kind.Value);
    }
}
