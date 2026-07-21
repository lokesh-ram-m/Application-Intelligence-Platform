using Aip.Abstractions.Ai;
using Aip.Abstractions.Analysis;
using Aip.Abstractions.Registries;
using Aip.Core.Domain;
using Aip.Registries;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aip.Host;

/// <summary>
/// Drives batch analysis (<c>run --config apps.yml</c>, and what <c>serve</c> mode's <c>/run</c> endpoint
/// calls internally): declares the estate in a file, registers every application, and normalizes each one
/// into an <see cref="ExecutionRequest"/> for the single pipeline. Repos may be git URLs (cloned) or local
/// paths. Every run auto-diffs against each repo's last analyzed commit (see <c>ExecutionPipeline</c>) —
/// there is no separate external trigger; this is the only way analysis ever runs.
/// Reports through <see cref="ILogger"/>, not <see cref="Console"/> — this runs unattended from
/// <c>serve</c>'s background <c>/run</c> task with no terminal watching, so every operational fact (a
/// warning, a failure, AI status) needs to land in the same Serilog→SQL Run History pipeline as everything
/// else, not just stdout. Serilog's console sink (see <c>Program.cs</c>) still surfaces it to a human
/// running the CLI <c>run</c> command directly.
/// </summary>
internal static class PlatformRunner
{
    public static async Task<int> RunBatchAsync(IServiceProvider provider, string configPath)
    {
        ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Aip.Host.PlatformRunner");

        IReadOnlyList<ApplicationDescriptor> apps = SeedRegistry(provider, configPath);
        if (apps.Count == 0) { logger.LogWarning("No applications declared in the config."); return 1; }

        PrepareOutput(provider);
        var pipeline = provider.GetRequiredService<IAnalysisPipeline>();

        logger.LogInformation("=== Batch analysis — {AppCount} application(s) from '{ConfigFile}' ===",
            apps.Count, Path.GetFileName(configPath));
        LogAiStatus(provider, logger);
        int failures = 0;
        foreach (ApplicationDescriptor app in TopologicalOrder(apps))
        {
            logger.LogInformation("▶ {AppName}  ({RepoCount} repo(s))", app.Name, app.Repositories.Count);
            ExecutionResult result = await pipeline.ExecuteAsync(
                new ExecutionRequest(new ApplicationId(app.Name), ExecutionMode.Local));
            LogResult(logger, result);
            if (result.Outcome == ExecutionOutcome.Failed) failures++;
        }

        logger.LogInformation("Documentation published for {AppCount} application(s): {Apps}",
            apps.Count, string.Join(", ", apps.Select(a => a.Name)));
        LogAiUsage(provider, logger);

        return failures == 0 ? 0 : 1;
    }

    // ---- shared helpers ----

    // Children-before-parents post-order over the Name -> Children graph, so a composite application
    // only runs once every child it pulls a snapshot from (ExecutionPipeline.ExecuteAsync) has already
    // committed this batch's snapshot. AppsFile.Load already guarantees the graph is acyclic and every
    // reference resolves, so this can assume a valid DAG without re-checking either invariant.
    internal static IReadOnlyList<ApplicationDescriptor> TopologicalOrder(IReadOnlyList<ApplicationDescriptor> apps)
    {
        var byName = apps.ToDictionary(a => a.Name, a => a);
        var visited = new HashSet<string>();
        var ordered = new List<ApplicationDescriptor>();

        void Visit(ApplicationDescriptor app)
        {
            if (!visited.Add(app.Name)) return;
            foreach (string child in app.Children)
                if (byName.TryGetValue(child, out ApplicationDescriptor? childApp))
                    Visit(childApp);
            ordered.Add(app);
        }

        foreach (ApplicationDescriptor app in apps) Visit(app);

        return ordered;
    }

    private static IReadOnlyList<ApplicationDescriptor> SeedRegistry(IServiceProvider provider, string configPath)
    {
        IReadOnlyList<ApplicationDescriptor> apps = AppsFile.Load(configPath);
        var registry = provider.GetRequiredService<SeedableApplicationRegistry>();
        foreach (ApplicationDescriptor app in apps) registry.Register(app);

        return apps;
    }

    private static void PrepareOutput(IServiceProvider provider)
    {
        string root = FindSolutionRoot() ?? Directory.GetCurrentDirectory();
        Environment.SetEnvironmentVariable("AIP_OUTPUT", Path.Combine(root, "output"));
    }

    // Tell the user, up front, whether documentation prose will be AI-written or deterministic — so a run's
    // quality is never a mystery. AI off is a prominent notice, not a silent fallback.
    private static void LogAiStatus(IServiceProvider provider, ILogger logger)
    {
        if (provider.GetRequiredService<IAiPlatform>().IsAvailable)
            logger.LogInformation("AI: ON — narrative pages will be AI-written (deterministic fallback per page if rate-limited).");
        else
            logger.LogWarning("AI: OFF — documentation will be DETERMINISTIC (structured, not prose). " +
                              "Enable it: add a GitHub Models token to appsettings.Development.json (gitignored) " +
                              "or set AIP_GITHUB_TOKEN. See the README's \"AI narrative\" section.");
    }

    // After the run, report what the AI actually did — 0 successful calls means every page fell back to
    // deterministic (usually rate-limiting), so the user knows the prose is deterministic even with AI ON.
    private static void LogAiUsage(IServiceProvider provider, ILogger logger)
    {
        if (!provider.GetRequiredService<IAiPlatform>().IsAvailable) return;
        int calls = provider.GetRequiredService<IAiExecutionHistory>().Records.Count;
        AiUsage total = provider.GetRequiredService<ITokenAccountant>().Total;
        if (calls > 0)
            logger.LogInformation("AI: {Calls} call(s), {Tokens} tokens — narrative pages were AI-written.", calls, total.Total);
        else
            logger.LogWarning("AI was ON but 0 calls succeeded (likely rate-limited) — all pages used DETERMINISTIC rendering.");
    }

    private static void LogResult(ILogger logger, ExecutionResult r)
    {
        logger.LogInformation(
            "    outcome={Outcome} discoveries={Discoveries} accepted={Accepted} nodesΔ={NodesDelta} relsΔ={RelsDelta} {DurationMs}ms snapshot={Snapshot}",
            r.Outcome, r.Discoveries.Count, r.Metrics.DiscoveriesAccepted, r.Metrics.NodesChanged,
            r.Metrics.RelationshipsChanged, r.Metrics.Duration.TotalMilliseconds, r.Snapshot);

        foreach (Diagnostic d in r.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).Take(6))
        {
            if (d.Severity == DiagnosticSeverity.Error)
                logger.LogError("[{Source}] {Message}", d.Source, d.Message);
            else
                logger.LogWarning("[{Source}] {Message}", d.Source, d.Message);
        }

        foreach (Diagnostic d in r.Diagnostics.Where(d => d.Source == DiagnosticSources.Pipeline))
            logger.LogInformation("    {Message}", d.Message);
    }

    // Walks up from the executable to the folder containing Aip.slnx — shared with Program.cs's own
    // need for the same solution-root resolution (apps.yml, appsettings.json, and persisted stores all
    // live there regardless of which project's bin/ is running).
    internal static string? FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Aip.slnx").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
