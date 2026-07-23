using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Core.Domain;
using Aip.Plugins.Security;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises <see cref="SecretScanAnalyzer"/> directly against a hand-built <see cref="PlainTextModel"/> —
/// no compilation/language engine needed, since the analyzer only pattern-matches raw text. A minimal fake
/// <see cref="IAnalysisContext"/> is enough; no internal access to the real pipeline's context is needed.
/// </summary>
public class SecretScanAnalyzerTests
{
    private sealed class FakeSink : IDiscoverySink
    {
        public List<NodeDiscovery> Nodes { get; } = new();
        public void Add(Discovery discovery) { if (discovery is NodeDiscovery n) Nodes.Add(n); }
        public void Report(Diagnostic diagnostic) { }
    }

    private sealed class FakeContext : IAnalysisContext
    {
        public FakeContext(string path, string text) => Model = new PlainTextModel(path, text);
        public ExecutionId ExecutionId => new(Guid.NewGuid());
        public ExecutionScope Scope => new(new ApplicationId("app"), new[] { new RepositoryId("r") }, Array.Empty<string>(), ExecutionMode.Local, null);
        public Artifact Artifact => new(new RepositoryId("r"), "x", "security-scan-target", "x");
        public RepositoryId Repository => new("r");
        public Commit Commit => new("c");
        public string Engine => "plaintext";
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

    private static List<NodeDiscovery> Scan(string path, string text)
    {
        var sink = new FakeSink();
        new SecretScanAnalyzer().AnalyzeAsync(new FakeContext(path, text), sink).GetAwaiter().GetResult();
        return sink.Nodes;
    }

    [Fact]
    public void Flags_a_credential_shaped_value_assigned_to_a_token_named_key()
    {
        // Wholly fabricated value — long and non-placeholder-shaped so it exercises the same
        // CredentialAssignment path a real secret would, without being (or resembling) any actual observed
        // credential. The detector itself is generic (key name + non-trivial literal length), so any made-up
        // value of the right shape validates the logic just as well as a real-looking one would.
        const string fakeValue = "FAKE-TEST-TOKEN-DO-NOT-USE-1234567890abcdefghijklmnopqrstuvwxyz";
        List<NodeDiscovery> found = Scan("azure-pipelines-prod.yml", $"System.AccessToken: '{fakeValue}'");

        Assert.Contains(found, n => n.Kind.Value == "Vulnerability");
        // The matched secret VALUE must never end up in a node property — only where it was found.
        Assert.All(found, n => Assert.DoesNotContain(fakeValue, n.Properties.Values));
    }

    [Fact]
    public void Flags_a_password_embedded_in_a_connection_string()
    {
        List<NodeDiscovery> found = Scan("appsettings.json",
            "\"DefaultConnection\": \"Server=tcp:db.example.com;Database=App;User Id=sa;Password=Sup3rSecretValue!;\"");

        Assert.Contains(found, n => n.Kind.Value == "Vulnerability" && n.Properties["key"] == "connection string password");
    }

    [Fact]
    public void Does_not_flag_a_connection_string_password_pointing_at_localhost()
    {
        // Same shape as the real false positive found against CleanArchitecture's appsettings.PostgreSQL.json
        // this session — an obvious local-dev placeholder, not a real leaked credential.
        List<NodeDiscovery> found = Scan("appsettings.PostgreSQL.json",
            "\"CleanArchitectureDb\": \"Server=127.0.0.1;Port=5432;Database=CleanArchitectureDb;Username=admin;Password=password;\"");

        Assert.Empty(found);
    }

    [Fact]
    public void Does_not_flag_this_project_s_own_placeholder_convention()
    {
        // The exact placeholder syntax appsettings.json in this repo already uses for real (see its own
        // header comment) — a negative test proving the scanner doesn't flag its own house style.
        List<NodeDiscovery> found = Scan("appsettings.json", "\"ApiKey\": \"~~{apiKey}~~\"");

        Assert.Empty(found);
    }

    [Fact]
    public void Does_not_flag_env_var_or_short_dummy_values()
    {
        List<NodeDiscovery> a = Scan("appsettings.json", "\"Secret\": \"${SECRET_VALUE}\"");
        List<NodeDiscovery> b = Scan("appsettings.json", "\"Password\": \"changeme\"");

        Assert.Empty(a);
        Assert.Empty(b);
    }
}
