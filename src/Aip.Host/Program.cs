using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;
using Aip.Abstractions.Projections;
using Aip.Core.Abstractions;
using Aip.Host;
using Aip.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Serilog;

// ==========================================================================
//  Application Intelligence Platform — Host (composition root + entry point)
//  Run with --help for the full mode/flag reference (see PrintHelp below).
// ==========================================================================

// ---- CLI validation (before any config/DI work — a bad invocation should fail instantly) ----

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintHelp();

    return 0;
}

if (args.Contains("--version"))
{
    Console.WriteLine(typeof(DomainVerification).Assembly.GetName().Version?.ToString() ?? "unknown");

    return 0;
}

string[] knownModes = { "verify", "demo", "run", "serve" };
string[] modesGiven = args.Where(a => knownModes.Contains(a)).ToArray();

if (modesGiven.Length > 1)
{
    Console.Error.WriteLine($"Conflicting modes given: {string.Join(", ", modesGiven)} — specify only one. Run with --help for usage.");

    return 1;
}

string[] knownFlags = { "--config" };
List<string> unknownFlags = args.Where(a => a.StartsWith("--") && a is not ("--help" or "--version") && !knownFlags.Contains(a)).ToList();
if (unknownFlags.Count > 0)
{
    Console.Error.WriteLine($"Unknown flag(s): {string.Join(", ", unknownFlags)}. Run with --help for usage.");

    return 1;
}

// Configuration layers, lowest to highest precedence: appsettings.json (committed, safe defaults/structure,
// no real secrets) → appsettings.Development.json (gitignored, real local secrets) → environment variables
// (always wins). This is exactly how production supplies secrets: a pipeline or App Service sets an env var
// from a vault, and this same code path picks it up automatically — no branching, no separate "prod mode".
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(PlatformRunner.FindSolutionRoot() ?? Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// One outer boundary around the whole CLI: without it, an exception from anywhere below (a bad connection
// string during composition, a construction-time failure, `serve`/`verify`'s own logic) would print a raw
// .NET stack trace and a generic exit code, rather than a clean message an operator can actually act on.
try
{
    // `verify` exercises the Core Domain (identity, invariants, lifecycle) and exits — no container.
    if (args.Contains("verify"))
        return DomainVerification.Run();

    string configPath = GetOption(args, "--config") ?? DefaultConfigPath();

    // `serve` is the standalone/unattended entry point — see Serve.cs. It builds its own ASP.NET Core
    // composition (needs the web host APIs the plain CLI flow below doesn't), so it branches out here
    // before any of that CLI-specific setup.
    if (args.Contains("serve"))
        return await Serve.RunAsync(configPath);

    bool isRun = args.Contains("run");

    var services = new ServiceCollection();
    services.AddApplicationIntelligencePlatform(configuration);
    // Serilog: console + the same SQL Server database Run History uses (a Logs table). Set up before the
    // container is even built, so construction-time failures are captured too, not just what happens after.
    services.AddAipLogging(configuration, "Aip.Host");

    using ServiceProvider provider = services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    try
    {
        // Run History (SQL Server / Azure SQL, via History:ConnectionString) — applies pending migrations, creating the
        // database on first run. Cheap and idempotent, so it's on by default; set History:AutoMigrate to false
        // (or AIP_HISTORY_AUTO_MIGRATE=false) to manage the schema yourself instead, e.g. via `dotnet ef database update`.
        // Deliberately fails fast here (no retry) — unlike `serve`'s long-lived process (see Serve.cs), a
        // short-lived CLI run has a human watching who can just retry the command.
        string? autoMigrate = Environment.GetEnvironmentVariable("AIP_HISTORY_AUTO_MIGRATE") ?? configuration["History:AutoMigrate"];
        if (autoMigrate is null || !bool.TryParse(autoMigrate, out bool autoMigrateEnabled) || autoMigrateEnabled)
            await provider.MigrateRunHistoryAsync();

        // `demo` runs the Analysis Platform end-to-end against the bundled sample application.
        if (args.Contains("demo"))
            return await DemoRunner.RunAsync(provider);

        // `run` drives a real batch analysis of every application in the config. The batch itself reports
        // through ILogger (see PlatformRunner) since it also runs unattended from `serve`; this hint is
        // pure CLI navigation for a human watching this terminal, so it stays a plain Console.WriteLine.
        if (isRun)
        {
            int exitCode = await PlatformRunner.RunBatchAsync(provider, configPath);
            if (exitCode == 0)
                Console.WriteLine("\nBrowse it live (no local build, reads the store on every request):\n    dotnet run --project src/Aip.Viewer");

            return exitCode;
        }

        // Default: resolve the key subsystems to prove the container is correctly composed. Pure local
        // dev/diagnostic output — never reached from `serve` — so plain Console.WriteLine is correct here.
        var pipeline = provider.GetRequiredService<IAnalysisPipeline>();
        var knowledge = provider.GetRequiredService<IKnowledgeRepository>();
        var projections = provider.GetRequiredService<IProjectionEngine>();
        var plugins = provider.GetServices<IPlugin>().ToList();
        var engines = provider.GetServices<ILanguageEngine>().ToList();

        Console.WriteLine($"""
            Application Intelligence Platform — Architecture v1.0

              Analysis pipeline   : {pipeline.GetType().Name}
              Knowledge repository: {knowledge.GetType().Name}
              Projection engine   : {projections.GetType().Name}
              Language engines    : {string.Join(", ", engines.Select(e => e.Language))}
              Plugins             : {string.Join(", ", plugins.Select(p => p.Manifest.Id))}

            Container composed and validated.
            Run an analysis:  dotnet run --project src/Aip.Host -- run --config apps.yml
            """);

        return 0;
    }
    finally
    {
        // Serilog sinks (the SQL Server one especially) batch/buffer — without an explicit flush, the last
        // batch of a short-lived CLI run could be lost when the process exits.
        await Log.CloseAndFlushAsync();
    }
}
catch (Exception ex)
{
    // stderr, not stdout — this is an operator-facing failure, not program output. Console.Error.WriteLine(ex)
    // still includes the full stack trace (needed for real debugging), just framed as a handled failure
    // rather than the runtime's own unhandled-exception dump.
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    Console.Error.WriteLine(ex);

    return 1;
}

// ---- argument helpers ----

static void PrintHelp()
{
    Console.WriteLine("""
        Application Intelligence Platform — Host

        Usage: dotnet run --project src/Aip.Host [-- <mode>] [options]

        Modes (specify at most one; none of them run analysis over a network by themselves — apps.yml
        declares the estate):
          (none)                   Compose + validate the service graph, then exit.
          verify                   Exercise the Core Domain invariants, then exit.
          demo                     End-to-end walkthrough against the bundled sample application.
          run                      Batch analysis of every app in the config, auto-diffed against each
                                    repo's last analyzed commit — full only on an app's first-ever run,
                                    skipped outright when nothing changed.
          serve                    Standalone/unattended: a normal running app exposing POST /run, which
                                    triggers the same batch analysis as `run` above. The intended
                                    production shape — see README's "Deployment" section.

        Options:
          --config <path>          Application registry file (default: apps.yml at the solution root).
          --help, -h               Show this message.
          --version                Show the assembly version.
        """);
}

// Returns the value after a flag (e.g. --config apps.yml → "apps.yml"), or null if the flag is absent.
static string? GetOption(string[] args, string name)
{
    int i = Array.IndexOf(args, name);

    return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
}

static string DefaultConfigPath() =>
    Path.Combine(PlatformRunner.FindSolutionRoot() ?? Directory.GetCurrentDirectory(), "apps.yml");
