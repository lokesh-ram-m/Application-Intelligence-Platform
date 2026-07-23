using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.React;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises <see cref="ReactComponentAnalyzer"/>'s <c>displayName</c> extraction — the page's own on-
/// screen heading, only attached when it's unambiguous (exactly one h1/h2 in the file) and plain text
/// (not a JS expression like <c>{title}</c>).
/// </summary>
public class ReactComponentAnalyzerTests
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
        public Artifact Artifact => new(new RepositoryId("r"), "x", "react-workspace", "x");
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
        await new ReactComponentAnalyzer().AnalyzeAsync(new FakeContext(files), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Extracts_the_single_unambiguous_heading_as_display_name()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("ContractMasters.tsx", """
            export default function ContractMasters() {
              return (
                <div>
                  <h1>Contract Masters</h1>
                  <p>Maintain values used by dropdowns.</p>
                </div>
              );
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("Contract Masters", n.Properties["displayName"]);
    }

    [Fact]
    public async Task Does_not_extract_when_multiple_headings_are_ambiguous()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Dashboard.tsx", """
            export default function Dashboard() {
              return (
                <div>
                  <h1>Dashboard</h1>
                  <h2>Recent Activity</h2>
                </div>
              );
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("displayName"));
    }

    [Fact]
    public async Task Does_not_extract_a_dynamic_expression_heading()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("Generic.tsx", """
            export default function Generic({ title }) {
              return <h1>{title}</h1>;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("displayName"));
    }

    [Fact]
    public async Task Extracts_empty_state_text_from_a_length_zero_conditional()
    {
        // Same shape as the real "My Obligations" screen confirmed via screenshot this session:
        // an icon, a heading, and a subtext, all inside the `=== 0` branch.
        List<NodeDiscovery> found = await Analyze(new TsFile("MyObligations.tsx", """
            export default function MyObligations() {
              return (
                <div>
                  {obligations.length === 0 ? (
                    <div className="empty">
                      <FileIcon />
                      <h3>No obligations found</h3>
                      <p>No obligations available</p>
                    </div>
                  ) : (
                    <Table items={obligations} />
                  )}
                </div>
              );
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("No obligations found", n.Properties["emptyStateLabel"]);
    }

    [Fact]
    public async Task Does_not_extract_when_multiple_empty_state_conditionals_are_ambiguous()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("TwoLists.tsx", """
            export default function TwoLists() {
              return (
                <div>
                  {a.length === 0 ? <p>No A</p> : <ListA items={a} />}
                  {b.length === 0 ? <p>No B</p> : <ListB items={b} />}
                </div>
              );
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("emptyStateLabel"));
    }

    [Fact]
    public async Task Does_not_extract_the_reversed_length_greater_than_zero_form()
    {
        // Deliberately unsupported (see the analyzer's own comment) — isolating content after the `:` in
        // this reversed form is a balanced-parsing problem regex can't safely solve.
        List<NodeDiscovery> found = await Analyze(new TsFile("Reversed.tsx", """
            export default function Reversed() {
              return items.length > 0 ? <Table items={items} /> : <p>No items yet</p>;
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.False(n.Properties.ContainsKey("emptyStateLabel"));
    }
}
