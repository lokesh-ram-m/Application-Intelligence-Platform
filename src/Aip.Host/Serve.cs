using Aip.Infrastructure;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Serilog;

namespace Aip.Host;

/// <summary>
/// The standalone/unattended entry point (<c>serve --config apps.yml</c>): a normal, addressable
/// ASP.NET Core app exposing one endpoint, <c>POST /run</c>, that triggers the exact same batch analysis
/// <see cref="PlatformRunner.RunBatchAsync"/> already performs for every application in the config —
/// kicked off on a background task so the HTTP call returns immediately rather than holding the
/// connection open for a run that can take many minutes. Intended to sit behind a daily external trigger
/// (see README) rather than a CI/CD pipeline; the trigger only needs to know how to make one HTTP call.
/// </summary>
internal static class Serve
{
    public static async Task<int> RunAsync(string configPath)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Same configuration layering as the CLI entry point (Program.cs) — solution-root-relative files,
        // not the exe's own directory, so both entry points always agree on where secrets/config live.
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .SetBasePath(PlatformRunner.FindSolutionRoot() ?? Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables();

        builder.Services.AddApplicationIntelligencePlatform(builder.Configuration);
        builder.Services.AddAipLogging(builder.Configuration, "Aip.Host");

        WebApplication app = builder.Build();

        string? autoMigrate = Environment.GetEnvironmentVariable("AIP_HISTORY_AUTO_MIGRATE") ?? builder.Configuration["History:AutoMigrate"];
        if (autoMigrate is null || !bool.TryParse(autoMigrate, out bool autoMigrateEnabled) || autoMigrateEnabled)
            await MigrateWithRetryAsync(app.Services, app.Logger);

        // A shared secret, not full auth infrastructure — there's exactly one trusted caller (the daily
        // trigger), so anything heavier would be ceremony without real added safety. Unset means "no check"
        // (local/dev convenience) — set it in production via Run:Secret or AIP_RUN_SECRET.
        string? runSecret = Environment.GetEnvironmentVariable("AIP_RUN_SECRET") ?? builder.Configuration["Run:Secret"];

        // Guards against two overlapping batch runs: PublishVersionAsync's version-index read-modify-write
        // (see ExecutionPipeline) is only safe with one writer at a time, and a genuine daily cadence should
        // never need two runs in flight anyway — a second call while one is running is almost certainly a
        // duplicate trigger, not a legitimate need to run again immediately.
        int running = 0;

        app.MapPost("/run", (HttpRequest request) =>
        {
            if (runSecret is not null && request.Headers["X-Run-Key"] != runSecret)
                return Results.Unauthorized();

            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return Results.Conflict(new { message = "A batch run is already in progress." });

            IServiceScope scope = app.Services.CreateScope();
            _ = Task.Run(async () =>
            {
                try { await PlatformRunner.RunBatchAsync(scope.ServiceProvider, configPath); }
                catch (Exception ex) { app.Logger.LogError(ex, "Background batch run failed."); }
                finally { Interlocked.Exchange(ref running, 0); scope.Dispose(); }
            });

            return Results.Accepted(value: new { message = "Batch run started." });
        });

        // Liveness (is the process itself alive — no dependency checks) vs. readiness (can it actually do
        // its job) are deliberately separate: a transient SQL blip should stop traffic being routed here,
        // not make an orchestrator conclude the process itself is dead and restart it.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => true });

        try { await app.RunAsync(); }
        finally { await Log.CloseAndFlushAsync(); }

        return 0;
    }

    // Unlike the CLI entry point (Program.cs, which fails fast — a human is watching and can just retry),
    // `serve` is a long-lived process: a transient SQL connectivity blip at startup shouldn't crash-loop it
    // when the same connection would very likely succeed a few seconds later.
    private static async Task MigrateWithRetryAsync(IServiceProvider services, Microsoft.Extensions.Logging.ILogger logger, int maxAttempts = 3)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await services.MigrateRunHistoryAsync();

                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(ex, "Run History migration failed (attempt {Attempt}/{MaxAttempts}) — retrying in {DelaySeconds}s.",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }
    }
}
