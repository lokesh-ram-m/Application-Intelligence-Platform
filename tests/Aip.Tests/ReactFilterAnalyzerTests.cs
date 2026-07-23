using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.React;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises ReactFilterAnalyzer's redesign — classifying filter state by shape (toggle/multi-select/tab/
/// single-value) instead of treating every "filter"-named useState identically, and tracing usage to ground
/// a targetField instead of only ever repeating the raw variable name.
/// </summary>
public class ReactFilterAnalyzerTests
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
        public Artifact Artifact => new(new RepositoryId("r"), "x", "react-app", "x");
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
        await new ReactFilterAnalyzer().AnalyzeAsync(new FakeContext(files), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Open_suffixed_toggle_state_is_not_captured_as_a_filter()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("AuditTrail.tsx", """
            function AuditTrail() {
              const [statusFilterOpen, setStatusFilterOpen] = useState(false);
              const [filterContractId, setFilterContractId] = useState('');
              return null;
            }
            """));

        Assert.DoesNotContain(found, n => n.Properties["name"] == "statusFilterOpen");
        Assert.Contains(found, n => n.Properties["name"] == "filterContractId");
    }

    [Fact]
    public async Task Selected_prefixed_plural_state_is_classified_multi_select()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("AuditTrail.tsx", """
            function AuditTrail() {
              const [selectedStatusFilters, setSelectedStatusFilters] = useState([]);
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("multi-select", n.Properties["kind"]);
    }

    [Fact]
    public async Task Tab_suffixed_state_is_classified_tab()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Billing.tsx", """
            function Billing() {
              const [nonBilledFilterTab, setNonBilledFilterTab] = useState('all');
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("tab", n.Properties["kind"]);
    }

    [Fact]
    public async Task Plain_filter_state_is_classified_single_value()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Contracts.tsx", """
            function Contracts() {
              const [filterContractId, setFilterContractId] = useState('');
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("single-value", n.Properties["kind"]);
    }

    [Fact]
    public async Task Filter_predicate_usage_grounds_the_target_field()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Contracts.tsx", """
            function Contracts() {
              const [filterContractId, setFilterContractId] = useState('');
              const rows = items.filter(item => item.contractId === filterContractId);
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("contractId", n.Properties["targetField"]);
    }

    [Fact]
    public async Task Query_string_interpolation_usage_grounds_the_target_field()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Contracts.tsx", """
            function Contracts() {
              const [filterContractId, setFilterContractId] = useState('');
              fetch(`/api/contracts?contractId=${filterContractId}`);
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("contractId", n.Properties["targetField"]);
    }

    [Fact]
    public async Task Filter_state_with_no_traceable_usage_is_still_captured_without_a_targetField()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Contracts.tsx", """
            function Contracts() {
              const [filterContractId, setFilterContractId] = useState('');
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("targetField"));
    }

    [Fact]
    public async Task ViewTab_state_without_filter_in_its_name_is_still_captured_when_it_drives_a_fetch()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Allocations.tsx", """
            function Allocations() {
              const [activeTab, setActiveTab] = useState('all');
              useEffect(() => { fetch(`/api/allocations?tab=${activeTab}`); }, [activeTab]);
              return null;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("view-tab", n.Properties["kind"]);
    }
}
