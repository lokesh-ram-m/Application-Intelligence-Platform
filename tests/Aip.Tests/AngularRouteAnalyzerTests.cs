using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.Angular;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises AngularRouteAnalyzer's structural parsing of route array objects — nested children joined into
/// a full path, lazy-loaded loadChildren module capture, and eager component capture — replacing the old
/// bare `path:` regex scan that couldn't tell a nested route's path from its parent's.
/// </summary>
public class AngularRouteAnalyzerTests
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

    private static async Task<List<NodeDiscovery>> Analyze(params TsFile[] files)
    {
        var sink = new FakeSink();
        await new AngularRouteAnalyzer().AnalyzeAsync(new FakeContext(files), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Nested_children_route_gets_a_full_parent_joined_path()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("app-routing.module.ts", """
            const routes: Routes = [
              {
                path: 'users',
                children: [
                  { path: '', component: UserListComponent },
                  { path: ':id', component: UserDetailComponent },
                ],
              },
            ];
            """));

        // The wrapping `{ path: 'users', children: [...] }` object has no component/loadChildren of its
        // own, so it's a pathless grouping wrapper only — no node is emitted for it, avoiding an identity
        // collision with the empty-path index child below (which resolves to that same full path).
        Assert.Equal(2, found.Count);
        NodeDiscovery listDefault = Assert.Single(found, n => n.Properties["path"] == "users");
        Assert.Equal("UserListComponent", listDefault.Properties["component"]);
        NodeDiscovery detail = Assert.Single(found, n => n.Properties["path"] == "users/:id");
        Assert.Equal("UserDetailComponent", detail.Properties["component"]);
    }

    [Fact]
    public async Task A_layout_route_with_its_own_component_is_still_emitted_alongside_its_children()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("app-routing.module.ts", """
            const routes: Routes = [
              {
                path: 'settings',
                component: SettingsLayoutComponent,
                children: [
                  { path: 'profile', component: ProfileComponent },
                ],
              },
            ];
            """));

        Assert.Equal(2, found.Count);
        NodeDiscovery layout = Assert.Single(found, n => n.Properties["path"] == "settings");
        Assert.Equal("SettingsLayoutComponent", layout.Properties["component"]);
        NodeDiscovery profile = Assert.Single(found, n => n.Properties["path"] == "settings/profile");
        Assert.Equal("ProfileComponent", profile.Properties["component"]);
    }

    [Fact]
    public async Task LazyLoaded_route_captures_the_imported_module()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("app-routing.module.ts", """
            const routes: Routes = [
              { path: 'admin', loadChildren: () => import('./admin/admin.module').then(m => m.AdminModule) },
            ];
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("admin", n.Properties["path"]);
        Assert.Equal("./admin/admin.module", n.Properties["loadChildren"]);
        Assert.Equal("AdminModule", n.Properties["loadChildrenExport"]);
    }

    [Fact]
    public async Task Eager_route_captures_its_component_and_not_a_loadChildren_property()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("app-routing.module.ts", """
            const routes: Routes = [
              { path: 'orders', component: OrdersComponent },
            ];
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("OrdersComponent", n.Properties["component"]);
        Assert.False(n.Properties.ContainsKey("loadChildren"));
    }
}
