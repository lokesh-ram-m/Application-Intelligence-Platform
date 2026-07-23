using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.Angular;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises AngularFilterAnalyzer — the Angular-side mirror of ReactFilterAnalyzer's redesign: classifying
/// component filter fields by shape (toggle/multi-select/tab/single-value) and tracing usage to ground a
/// targetField, adjusted for Angular's `this.` member-access convention and class-field state shape.
/// </summary>
public class AngularFilterAnalyzerTests
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
        await new AngularFilterAnalyzer().AnalyzeAsync(new FakeContext(files), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Open_suffixed_toggle_field_is_not_captured_as_a_filter()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("audit-trail.component.ts", """
            @Component({ selector: 'app-audit-trail' })
            export class AuditTrailComponent {
              statusFilterOpen = false;
              filterContractId = '';
            }
            """));

        Assert.DoesNotContain(found, n => n.Properties["name"] == "statusFilterOpen");
        Assert.Contains(found, n => n.Properties["name"] == "filterContractId");
    }

    [Fact]
    public async Task Selected_prefixed_plural_field_is_classified_multi_select()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("audit-trail.component.ts", """
            @Component({ selector: 'app-audit-trail' })
            export class AuditTrailComponent {
              selectedStatusFilters: string[] = [];
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("multi-select", n.Properties["kind"]);
    }

    [Fact]
    public async Task Tab_suffixed_field_is_classified_tab()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("billing.component.ts", """
            @Component({ selector: 'app-billing' })
            export class BillingComponent {
              nonBilledFilterTab = 'all';
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("tab", n.Properties["kind"]);
    }

    [Fact]
    public async Task Plain_filter_field_is_classified_single_value()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("contracts.component.ts", """
            @Component({ selector: 'app-contracts' })
            export class ContractsComponent {
              filterContractId = '';
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("single-value", n.Properties["kind"]);
    }

    [Fact]
    public async Task Filter_predicate_usage_grounds_the_target_field()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("contracts.component.ts", """
            @Component({ selector: 'app-contracts' })
            export class ContractsComponent {
              filterContractId = '';
              get rows() { return this.items.filter(item => item.contractId === this.filterContractId); }
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("contractId", n.Properties["targetField"]);
    }

    [Fact]
    public async Task Query_string_interpolation_usage_grounds_the_target_field()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("contracts.component.ts", """
            @Component({ selector: 'app-contracts' })
            export class ContractsComponent {
              filterContractId = '';
              load() { this.http.get(`/api/contracts?contractId=${this.filterContractId}`); }
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("contractId", n.Properties["targetField"]);
    }

    [Fact]
    public async Task Filter_field_with_no_traceable_usage_is_still_captured_without_a_targetField()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("contracts.component.ts", """
            @Component({ selector: 'app-contracts' })
            export class ContractsComponent {
              filterContractId = '';
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("targetField"));
    }

    [Fact]
    public async Task A_filter_named_field_on_a_non_component_class_is_not_captured()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("search-request.model.ts", """
            export class SearchRequest {
              filterContractId = '';
            }
            """));

        Assert.Empty(found);
    }
}
