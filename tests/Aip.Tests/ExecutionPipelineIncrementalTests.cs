using Aip.Abstractions.Analysis;
using Aip.Abstractions.Documents;
using Aip.Abstractions.History;
using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Registries;
using Aip.Core.Domain;
using Aip.Host;
using Aip.Infrastructure;
using Aip.Registries;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises <see cref="ExecutionPipeline"/>'s use of a repository's reported <c>ChangedFiles</c>/commit —
/// distinct from <see cref="GitRepositorySourceTests"/>, which tests whether the diff itself is computed
/// correctly. Here the diff is scripted (a fake <see cref="IRepositorySource"/>), so what's under test is
/// purely the pipeline's own decision-making: does an unchanged repo's knowledge survive untouched, does a
/// changed repo actually get re-analyzed, and does a version with no real Knowledge change skip publishing.
/// </summary>
public class ExecutionPipelineIncrementalTests
{
    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();
    private const string LocalSqlServer = "Server=localhost,1433;User Id=sa;Password=Aip_Local_Dev_2026!;TrustServerCertificate=True;";

    private static string NewTestConnectionString(out string databaseName)
    {
        databaseName = "AipHistoryTest_" + Guid.NewGuid().ToString("N");

        return $"{LocalSqlServer}Database={databaseName};";
    }

    private static async Task DropTestDatabaseAsync(string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection($"{LocalSqlServer}Database=master;");
        await master.OpenAsync();
        await using var cmd = new SqlCommand(
            $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END", master);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Aip.slnx").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Aip.slnx not found");
    }

    // Materializes real local paths (so artifact discovery/analysis is real) but lets the test dictate the
    // commit/ChangedFiles per call, so ExecutionPipeline's own pruning/carry-forward logic is what's under
    // test — not whether git diffing itself works (see GitRepositorySourceTests for that).
    private sealed class ScriptedRepositorySource : IRepositorySource
    {
        private readonly Dictionary<string, Queue<(string Commit, IReadOnlyList<string>? ChangedFiles)>> _script = new();

        public void Script(string location, string commit, IReadOnlyList<string>? changedFiles)
        {
            if (!_script.TryGetValue(location, out Queue<(string, IReadOnlyList<string>?)>? q))
                _script[location] = q = new Queue<(string, IReadOnlyList<string>?)>();
            q.Enqueue((commit, changedFiles));
        }

        public Task<RepositoryMaterialization> MaterializeAsync(RepositoryId repository, string location, string? previousCommit = null, CancellationToken ct = default)
        {
            (string commit, IReadOnlyList<string>? changedFiles) = _script[location].Dequeue();

            return Task.FromResult(new RepositoryMaterialization(repository, location, new Commit(commit), RepositorySourceKind.Local, changedFiles));
        }
    }

    [Fact]
    public async Task Unchanged_repo_is_pruned_and_its_knowledge_survives_while_the_changed_repo_is_reanalyzed()
    {
        string root = SolutionRoot();
        string backend = Path.Combine(root, "samples", "backend");
        string frontend = Path.Combine(root, "samples", "frontend");
        string temp = Path.Combine(Path.GetTempPath(), "aip-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("AIP_OUTPUT", temp);
        string connectionString = NewTestConnectionString(out string databaseName);
        Environment.SetEnvironmentVariable("AIP_SQL_CONNECTION_STRING", connectionString);

        try
        {
            var scripted = new ScriptedRepositorySource();
            var services = new ServiceCollection();
            services.AddApplicationIntelligencePlatform(EmptyConfig);
            services.AddSingleton<IRepositorySource>(scripted);   // overrides GitRepositorySource — last registration wins
            using ServiceProvider provider = services.BuildServiceProvider();
            await provider.MigrateRunHistoryAsync();
            provider.GetRequiredService<SeedableApplicationRegistry>()
                .Register(new ApplicationDescriptor("ShopApp", new[] { backend, frontend }));

            var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
            var store = provider.GetRequiredService<IKnowledgeStore>();
            var app = new ApplicationId("ShopApp");

            // Run 1 — first time for both repos; ChangedFiles is irrelevant (no previous commit on record yet,
            // so both are treated as fully changed regardless).
            scripted.Script(backend, "c1", null);
            scripted.Script(frontend, "c1", null);
            ExecutionResult run1 = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            Assert.Equal(ExecutionOutcome.Success, run1.Outcome);

            // Run 2 — backend changed (one real file touched), frontend reports the SAME commit as before
            // (genuinely unchanged) and should be pruned entirely, carrying its prior knowledge forward.
            scripted.Script(backend, "c2", new[] { "CustomerController.cs" });
            scripted.Script(frontend, "c1", null);
            ExecutionResult run2 = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            Assert.Equal(ExecutionOutcome.Success, run2.Outcome);

            // Something was pruned (frontend) and the pipeline still recognizes this as incremental.
            Diagnostic? modeLine = run2.Diagnostics.FirstOrDefault(d => d.Source == DiagnosticSources.Pipeline && d.Message.StartsWith("Mode:"));
            Assert.NotNull(modeLine);
            Assert.Contains("incremental", modeLine!.Message);
            Assert.DoesNotContain("pruned 0.", modeLine.Message);

            // Frontend's knowledge (never reanalyzed in run 2) still made it into the merged snapshot, and
            // backend's knowledge (freshly reanalyzed) is still there too — neither side was lost.
            Snapshot? snapshot = await store.GetSnapshotAsync(app);
            Assert.NotNull(snapshot);
            Assert.Contains(snapshot!.Nodes, n => n.Kind.Value == "UIComponent");   // carried forward from frontend
            Assert.Contains(snapshot.Nodes, n => n.Kind.Value == "Controller");     // freshly reanalyzed from backend
        }
        finally
        {
            // Runs even on assertion failure — otherwise a failing test leaks its throwaway database forever.
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Publish_is_skipped_when_nothing_under_any_repo_actually_changed()
    {
        string root = SolutionRoot();
        string backend = Path.Combine(root, "samples", "backend");
        string temp = Path.Combine(Path.GetTempPath(), "aip-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("AIP_OUTPUT", temp);
        string connectionString = NewTestConnectionString(out string databaseName);
        Environment.SetEnvironmentVariable("AIP_SQL_CONNECTION_STRING", connectionString);

        try
        {
            var scripted = new ScriptedRepositorySource();
            var services = new ServiceCollection();
            services.AddApplicationIntelligencePlatform(EmptyConfig);
            services.AddSingleton<IRepositorySource>(scripted);
            using ServiceProvider provider = services.BuildServiceProvider();
            await provider.MigrateRunHistoryAsync();
            // SkipIfUnchanged stays false (default) — the point is to hit the *new*, finer-grained "the
            // resulting Knowledge diff is empty" skip, not the pre-existing coarse commit-comparison one.
            provider.GetRequiredService<SeedableApplicationRegistry>()
                .Register(new ApplicationDescriptor("ShopApp", new[] { backend }));

            var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
            var documentStore = provider.GetRequiredService<IDocumentStore>();
            var versionChanges = provider.GetRequiredService<IVersionChangeStore>();
            var app = new ApplicationId("ShopApp");

            scripted.Script(backend, "c1", null);
            ExecutionResult run1 = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            Assert.Equal(ExecutionOutcome.Success, run1.Outcome);
            Assert.NotNull(await documentStore.ReadAsync("ShopApp", "v1/product-specification/overview.md"));

            // Reports the SAME commit as before — every artifact gets pruned, nothing fresh is discovered, and
            // MergeNodes/MergeRelationships carry the previous snapshot forward completely unchanged.
            scripted.Script(backend, "c1", null);
            ExecutionResult run2 = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            Assert.Equal(ExecutionOutcome.Success, run2.Outcome);

            Assert.Contains(run2.Diagnostics, d => d.Source == DiagnosticSources.Pipeline && d.Message.Contains("Publish skipped"));
            Assert.Null(await documentStore.ReadAsync("ShopApp", "v2/product-specification/overview.md"));   // no new version published
            Assert.Null(await versionChanges.GetAsync("ShopApp", 2));   // nothing to record either
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await DropTestDatabaseAsync(databaseName);
        }
    }

    // Copies samples/backend (skipping bin/obj) into an isolated temp directory so this test can add a
    // real new file without ever touching the shared sample the other tests analyze — mutating that
    // in place would risk racing (or permanently corrupting) EndToEndTests' assertions about its exact
    // node counts.
    private static string CopySampleBackend(string root, string destination)
    {
        string source = Path.Combine(root, "samples", "backend");
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(dir);
            if (name is "bin" or "obj") continue;
            Directory.CreateDirectory(dir.Replace(source, destination));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            File.Copy(file, file.Replace(source, destination));
        }

        return destination;
    }

    [Fact]
    public async Task A_real_structural_change_records_a_document_version_change_with_a_summary()
    {
        string root = SolutionRoot();
        string temp = Path.Combine(Path.GetTempPath(), "aip-test-" + Guid.NewGuid().ToString("N"));
        string backend = CopySampleBackend(root, Path.Combine(temp, "backend"));
        Environment.SetEnvironmentVariable("AIP_OUTPUT", Path.Combine(temp, "output"));
        string connectionString = NewTestConnectionString(out string databaseName);
        Environment.SetEnvironmentVariable("AIP_SQL_CONNECTION_STRING", connectionString);

        try
        {
            var scripted = new ScriptedRepositorySource();
            var services = new ServiceCollection();
            services.AddApplicationIntelligencePlatform(EmptyConfig);
            services.AddSingleton<IRepositorySource>(scripted);
            using ServiceProvider provider = services.BuildServiceProvider();
            await provider.MigrateRunHistoryAsync();
            provider.GetRequiredService<SeedableApplicationRegistry>()
                .Register(new ApplicationDescriptor("ShopApp", new[] { backend }));

            var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
            var versionChanges = provider.GetRequiredService<IVersionChangeStore>();
            var app = new ApplicationId("ShopApp");

            scripted.Script(backend, "c1", null);
            await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));

            // A genuine structural change — a brand-new controller the first run never saw, so this is a real
            // added Controller/Endpoint, not just a re-timestamped re-analysis of unchanged source.
            await File.WriteAllTextAsync(Path.Combine(backend, "Controllers", "PingController.cs"), """
                using Microsoft.AspNetCore.Mvc;

                namespace ShopApi.Controllers;

                [ApiController]
                [Route("api/[controller]")]
                public class PingController : ControllerBase
                {
                    [HttpGet]
                    public string Get() => "pong";
                }
                """);
            scripted.Script(backend, "c2", new[] { "PingController.cs" });
            ExecutionResult run2 = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            Assert.Equal(ExecutionOutcome.Success, run2.Outcome);

            DocumentVersionChange? change = await versionChanges.GetAsync("ShopApp", 2);
            Assert.NotNull(change);
            Assert.Equal(1, change!.PreviousVersionNumber);
            Assert.True(change.NodesAdded > 0);
            Assert.False(string.IsNullOrWhiteSpace(change.Summary));
            // No AI provider configured in these tests (EmptyConfig) — the changelog falls back to the
            // deterministic structured summary, same resilience path as every other AI-written artifact.
            Assert.False(change.AiWritten);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await DropTestDatabaseAsync(databaseName);
        }
    }
}
