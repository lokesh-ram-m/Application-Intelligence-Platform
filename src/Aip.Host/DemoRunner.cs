using Aip.Abstractions.Ai;
using Aip.Abstractions.Analysis;
using Aip.Abstractions.Documents;
using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Query;
using Aip.Abstractions.Registries;
using Aip.Core.Domain;
using Aip.Observability;
using Aip.Registries;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Host;

/// <summary>
/// Drives the complete platform end-to-end against the bundled sample application and prints the full
/// workflow: discovery → analysis → validation → knowledge → snapshot → resolution → projection →
/// document store, plus a query demo and an incremental re-run. Browsing the result is the Document
/// Viewer's job (Aip.Viewer) — it reads live from the same store, no local site is generated here.
/// </summary>
internal static class DemoRunner
{
    public static async Task<int> RunAsync(IServiceProvider provider)
    {
        string? root = PlatformRunner.FindSolutionRoot();
        if (root is null) { Console.WriteLine("Could not locate the solution root (Aip.slnx)."); return 1; }

        string output = Path.Combine(root, "output");
        Environment.SetEnvironmentVariable("AIP_OUTPUT", output);

        string backend = Path.Combine(root, "samples", "backend");
        string frontend = Path.Combine(root, "samples", "frontend");
        var app = new ApplicationId("ShopApp");

        provider.GetRequiredService<SeedableApplicationRegistry>()
            .Register(new ApplicationDescriptor("ShopApp", new[] { backend, frontend }));

        var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
        var store = provider.GetRequiredService<IKnowledgeStore>();
        var query = provider.GetRequiredService<IQueryPlatform>();

        Console.WriteLine("=== Application Intelligence Platform — end-to-end ===\n");
        Console.WriteLine("Application Folder → Repository Discovery → Analysis → Discoveries → Validation");
        Console.WriteLine("→ Knowledge Repository → Snapshot → Relationship Resolution → Projection → Document Store\n");

        // ---- FULL ANALYSIS ----
        ExecutionResult result = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
        PrintResult("Full analysis", result);

        Snapshot? snapshot = await store.GetSnapshotAsync(app);
        if (snapshot is null) { Console.WriteLine("No snapshot committed."); return 1; }

        Console.WriteLine("\nKnowledge Snapshot (committed by Validation → Knowledge Repository):");
        foreach (IGrouping<string, KnowledgeNode> g in snapshot.Nodes.GroupBy(n => n.Kind.Value).OrderBy(g => g.Key))
            Console.WriteLine($"    {g.Key,-14} {g.Count()}");
        Console.WriteLine($"    nodes={snapshot.Nodes.Count} relationships={snapshot.Relationships.Count}");

        Console.WriteLine("\nResolved relationships (Relationship Resolution Engine):");
        foreach (Relationship r in snapshot.Relationships.Where(r => r.Type.Value is "MAPS_TO" or "USES" or "DEPENDS_ON"))
            Console.WriteLine($"    {Label(r.From)}  --{r.Type.Value}-->  {Label(r.To)}   [conf {r.Confidence.Value:0.00}]");

        // ---- QUERY PLATFORM ----
        Console.WriteLine("\nQuery Platform:");
        KnowledgeNode? endpoint = snapshot.Nodes.FirstOrDefault(n => n.Kind.Value == "Endpoint" && n.Properties.GetValueOrDefault("route", "").Contains("Customer"));
        if (endpoint is not null)
        {
            IReadOnlyList<KnowledgeIdentity> impacted = await query.ImpactAsync(app, endpoint.Identity);
            Console.WriteLine($"    impact of '{Label(endpoint.Identity)}': {string.Join(", ", impacted.Select(Label))}");
        }
        Console.WriteLine($"    search 'customer': {(await query.SearchAsync(app, "customer")).Count} nodes");
        Console.WriteLine($"    snapshots: {(await query.SnapshotsAsync(app)).Count}");

        // ---- DOCUMENTATION (published to the document store; the Viewer reads it live, nothing local) ----
        var documentStore = provider.GetRequiredService<IDocumentStore>();
        IReadOnlyList<string> docs = await documentStore.ListAsync("ShopApp");
        Console.WriteLine("\nGenerated documentation (from the Knowledge Model only):");
        foreach (string path in docs.Where(p => p != DocumentVersionsIndex.FileName && !p.EndsWith(DocumentManifest.FileName, StringComparison.Ordinal)).OrderBy(p => p))
            Console.WriteLine($"    {path}");
        Console.WriteLine($"    Document store: {documentStore.GetType().Name}");
        Console.WriteLine("    Browse it live:  dotnet run --project src/Aip.Viewer  →  /shopapp/product-specification/overview");

        // ---- SECOND RUN (auto-diffed — nothing has changed since the first) ----
        Console.WriteLine();
        ExecutionResult second = await pipeline.ExecuteAsync(new ExecutionRequest(app, ExecutionMode.Local));
        PrintResult("Second run (nothing changed since the last analyzed commit)", second);
        Console.WriteLine($"    snapshots now: {(await query.SnapshotsAsync(app)).Count}");

        // ---- OBSERVABILITY ----
        var metrics = provider.GetRequiredService<MetricsCollector>();
        var tokens = provider.GetRequiredService<ITokenAccountant>();
        var aiHistory = provider.GetRequiredService<IAiExecutionHistory>();
        Console.WriteLine("\nObservability:");
        foreach (KeyValuePair<string, long> c in metrics.Counters.OrderBy(c => c.Key))
            Console.WriteLine($"    {c.Key} = {c.Value}");
        Console.WriteLine($"    tokens: {tokens.Total.Total} (AI calls: {aiHistory.Records.Count})");

        Console.WriteLine("\nWorkflow complete — knowledge built, snapshot committed, documentation published.");

        return 0;
    }

    private static void PrintResult(string label, ExecutionResult r)
    {
        Console.WriteLine($"[{label}]");
        Console.WriteLine($"    outcome={r.Outcome}  discoveries={r.Discoveries.Count}  " +
                          $"accepted={r.Metrics.DiscoveriesAccepted}  nodesΔ={r.Metrics.NodesChanged}  " +
                          $"relsΔ={r.Metrics.RelationshipsChanged}  {r.Metrics.Duration.TotalMilliseconds:0}ms  snapshot={r.Snapshot}");
        foreach (Diagnostic d in r.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).Take(6))
            Console.WriteLine($"    [{d.Severity}] {d.Source}: {d.Message}");
        foreach (Diagnostic d in r.Diagnostics.Where(d => d.Source == DiagnosticSources.Pipeline))
            Console.WriteLine($"    {d.Message}");
    }

    private static string Label(KnowledgeIdentity id) => id.ShortName;
}
