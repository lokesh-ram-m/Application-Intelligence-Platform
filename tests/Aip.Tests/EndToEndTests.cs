using Aip.Abstractions.Analysis;
using Aip.Abstractions.Documents;
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

public class EndToEndTests
{
    // No appsettings/env vars needed for these tests — an empty configuration keeps the AI provider on
    // its NoOp/deterministic path, same as today's behavior with no AIP_GITHUB_TOKEN set.
    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();

    // Run History now requires a real SQL Server — these tests target the local Docker SQL Server
    // container described in README.md, one throwaway database per test run so tests never collide or
    // leave stale state behind for the next run.
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

    [Fact]
    public async Task Full_pipeline_builds_knowledge_and_documentation()
    {
        string root = SolutionRoot();
        string temp = Path.Combine(Path.GetTempPath(), "aip-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("AIP_OUTPUT", temp);
        string connectionString = NewTestConnectionString(out string databaseName);
        Environment.SetEnvironmentVariable("AIP_SQL_CONNECTION_STRING", connectionString);

        try
        {
            var services = new ServiceCollection();
            services.AddApplicationIntelligencePlatform(EmptyConfig);
            using ServiceProvider provider = services.BuildServiceProvider();
            await provider.MigrateRunHistoryAsync();

            provider.GetRequiredService<SeedableApplicationRegistry>().Register(new ApplicationDescriptor(
                "ShopApp", new[] { Path.Combine(root, "samples", "backend"), Path.Combine(root, "samples", "frontend") }));

            var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
            ExecutionResult result = await pipeline.ExecuteAsync(new ExecutionRequest(new ApplicationId("ShopApp"), ExecutionMode.Local));

            Assert.Equal(ExecutionOutcome.Success, result.Outcome);
            Assert.NotNull(result.Snapshot);

            var store = provider.GetRequiredService<IKnowledgeStore>();
            Snapshot? snapshot = await store.GetSnapshotAsync(new ApplicationId("ShopApp"));
            Assert.NotNull(snapshot);

            // Semantic detection produced canonical, deduplicated knowledge.
            Assert.Contains(snapshot!.Nodes, n => n.Kind.Value == "Controller");
            Assert.Contains(snapshot.Nodes, n => n.Kind.Value == "Endpoint");
            Assert.Contains(snapshot.Nodes, n => n.Kind.Value == "UIComponent");
            Assert.Single(snapshot.Nodes, n => n.Kind.Value == "Service"); // no FQ/simple duplicate
            Assert.Contains(snapshot.Relationships, r => r.Type.Value == "EXPOSES");
            Assert.Contains(snapshot.Relationships, r => r.Type.Value == "MAPS_TO"); // cross-repo resolution

            // Documentation was published from the Knowledge Model into the document store — the real
            // contract the Viewer reads from live, not an incidental local file location.
            var documentStore = provider.GetRequiredService<IDocumentStore>();
            string? overview = await documentStore.ReadAsync("ShopApp", "v1/product-specification/overview.md");
            Assert.NotNull(overview);
        }
        finally
        {
            // Runs even on assertion failure — otherwise a failing test leaks its throwaway database forever.
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await DropTestDatabaseAsync(databaseName);
        }
    }

    // Real repos under test control (a scripted diff is what ExecutionPipelineIncrementalTests uses to
    // exercise partial pruning precisely) — samples/backend and samples/frontend are part of this very
    // repo, so two consecutive runs against them report the same real commit both times. That's still a
    // meaningful, real end-to-end case: it proves append-only Snapshot versioning holds even when the
    // second run finds nothing new to analyze (everything pruned).
    [Fact]
    public async Task Second_run_against_unchanged_repos_still_appends_a_snapshot()
    {
        string root = SolutionRoot();
        string temp = Path.Combine(Path.GetTempPath(), "aip-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("AIP_OUTPUT", temp);
        string connectionString = NewTestConnectionString(out string databaseName);
        Environment.SetEnvironmentVariable("AIP_SQL_CONNECTION_STRING", connectionString);

        try
        {
            var services = new ServiceCollection();
            services.AddApplicationIntelligencePlatform(EmptyConfig);
            using ServiceProvider provider = services.BuildServiceProvider();
            await provider.MigrateRunHistoryAsync();
            provider.GetRequiredService<SeedableApplicationRegistry>().Register(new ApplicationDescriptor(
                "ShopApp", new[] { Path.Combine(root, "samples", "backend"), Path.Combine(root, "samples", "frontend") }));

            var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
            var store = provider.GetRequiredService<IKnowledgeStore>();
            var app = new ApplicationId("ShopApp");

            await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));

            IReadOnlyList<Snapshot> history = await store.GetHistoryAsync(app);
            Assert.Equal(2, history.Count); // append-only versioning
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Snapshot_persists_across_store_instances()
    {
        string root = SolutionRoot();
        string temp = Path.Combine(Path.GetTempPath(), "aip-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("AIP_OUTPUT", temp);
        string connectionString = NewTestConnectionString(out string databaseName);
        Environment.SetEnvironmentVariable("AIP_SQL_CONNECTION_STRING", connectionString);
        var app = new ApplicationId("ShopApp");

        try
        {
            // Run 1 — build knowledge and commit with one store instance (simulates the first process).
            {
                var services = new ServiceCollection();
                services.AddApplicationIntelligencePlatform(EmptyConfig);
                using ServiceProvider provider = services.BuildServiceProvider();
                await provider.MigrateRunHistoryAsync();
                provider.GetRequiredService<SeedableApplicationRegistry>().Register(new ApplicationDescriptor(
                    "ShopApp", new[] { Path.Combine(root, "samples", "backend"), Path.Combine(root, "samples", "frontend") }));
                await provider.GetRequiredService<IAnalysisPipeline>()
                    .ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
            }

            // Run 2 — a brand-new store instance (fresh cache) loads the committed snapshot from SQL Server,
            // proving the Knowledge Store is real cross-process state, not an in-memory artifact of run 1.
            {
                var services = new ServiceCollection();
                services.AddApplicationIntelligencePlatform(EmptyConfig);
                using ServiceProvider provider = services.BuildServiceProvider();
                var store = provider.GetRequiredService<IKnowledgeStore>();
                Snapshot? loaded = await store.GetSnapshotAsync(app);

                Assert.NotNull(loaded);
                Assert.Contains(loaded!.Nodes, n => n.Kind.Value == "Endpoint");
                Assert.Contains(loaded.Relationships, r => r.Type.Value == "EXPOSES");
            }
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await DropTestDatabaseAsync(databaseName);
        }
    }
}
