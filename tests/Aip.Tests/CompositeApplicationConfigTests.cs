using Aip.Abstractions.Registries;
using Aip.Host;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Pure, fast tests for the composite-application config surface — <see cref="AppsFile"/>'s validation
/// (every child reference must exist; the composition graph must be acyclic) and
/// <see cref="PlatformRunner"/>'s topological batch ordering. No SQL Server, no repository materialization
/// — see <see cref="ExecutionPipelineIncrementalTests"/> for the end-to-end cascade behavior these two
/// pieces exist to make safe.
/// </summary>
public class CompositeApplicationConfigTests
{
    private static string WriteTempAppsYml(string yaml)
    {
        string path = Path.Combine(Path.GetTempPath(), "aip-appsyml-test-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, yaml);

        return path;
    }

    [Fact]
    public void Load_throws_when_a_child_references_an_undeclared_application()
    {
        string path = WriteTempAppsYml("""
            applications:
              - name: Portal
                children: [Backend]
            """);
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AppsFile.Load(path));
            Assert.Contains("Portal", ex.Message);
            Assert.Contains("Backend", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_throws_on_a_composition_cycle()
    {
        string path = WriteTempAppsYml("""
            applications:
              - name: A
                children: [B]
              - name: B
                children: [A]
            """);
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AppsFile.Load(path));
            Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_accepts_a_valid_composition_and_a_node_with_both_repos_and_children()
    {
        string path = WriteTempAppsYml("""
            applications:
              - name: Backend
                repos: [https://example.com/backend.git]
              - name: Frontend
                repos: [https://example.com/frontend.git]
              - name: Portal
                repos: [https://example.com/portal-shell.git]
                children: [Backend, Frontend]
            """);
        try
        {
            IReadOnlyList<ApplicationDescriptor> apps = AppsFile.Load(path);
            ApplicationDescriptor portal = Assert.Single(apps, a => a.Name == "Portal");
            Assert.Equal(new[] { "Backend", "Frontend" }, portal.Children);
            Assert.Single(portal.Repositories);
            Assert.Empty(apps.Single(a => a.Name == "Backend").Children);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TopologicalOrder_always_places_children_before_parents_regardless_of_declaration_order()
    {
        // Declared out of order on purpose: the parent-most node first, its child last.
        var apps = new List<ApplicationDescriptor>
        {
            new("Portal", Array.Empty<string>(), Children: new[] { "MidTier" }),
            new("MidTier", Array.Empty<string>(), Children: new[] { "Leaf" }),
            new("Leaf", Array.Empty<string>()),
        };

        IReadOnlyList<ApplicationDescriptor> ordered = PlatformRunner.TopologicalOrder(apps);

        int leaf = IndexOf(ordered, "Leaf");
        int mid = IndexOf(ordered, "MidTier");
        int portal = IndexOf(ordered, "Portal");
        Assert.True(leaf < mid, "Leaf must be processed before its parent MidTier.");
        Assert.True(mid < portal, "MidTier must be processed before its parent Portal.");
    }

    private static int IndexOf(IReadOnlyList<ApplicationDescriptor> apps, string name) =>
        apps.ToList().FindIndex(a => a.Name == name);
}
