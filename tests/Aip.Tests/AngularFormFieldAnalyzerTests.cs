using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.Angular;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises AngularFormFieldAnalyzer — reactive-form field names and their Validators.* calls, whichever
/// of the two conventional shapes (array-literal control or `new FormControl(...)`) is used.
/// </summary>
public class AngularFormFieldAnalyzerTests
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
        await new AngularFormFieldAnalyzer().AnalyzeAsync(new FakeContext(files), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task Fb_group_array_literal_fields_capture_their_validators()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("signup.component.ts", """
            export class SignupComponent {
              form = this.fb.group({
                name: ['', [Validators.required, Validators.minLength(3)]],
                email: ['', Validators.email],
              });
            }
            """));

        NodeDiscovery name = Assert.Single(found, n => n.Properties["name"] == "name");
        Assert.Equal("SignupComponent", name.Properties["form"]);
        Assert.Equal("required, minLength(3)", name.Properties["validation"]);

        NodeDiscovery email = Assert.Single(found, n => n.Properties["name"] == "email");
        Assert.Equal("email", email.Properties["validation"]);
    }

    [Fact]
    public async Task New_FormGroup_with_FormControl_fields_are_also_captured()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("login.component.ts", """
            export class LoginComponent {
              form = new FormGroup({
                username: new FormControl('', Validators.required),
              });
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("username", n.Properties["name"]);
        Assert.Equal("LoginComponent", n.Properties["form"]);
        Assert.Equal("required", n.Properties["validation"]);
    }

    [Fact]
    public async Task A_field_with_no_validators_is_still_captured_without_a_validation_property()
    {
        List<NodeDiscovery> found = await Analyze(new TsFile("search.component.ts", """
            export class SearchComponent {
              form = this.fb.group({
                query: [''],
              });
            }
            """));

        NodeDiscovery n = Assert.Single(found);
        Assert.Equal("query", n.Properties["name"]);
        Assert.False(n.Properties.ContainsKey("validation"));
    }
}
