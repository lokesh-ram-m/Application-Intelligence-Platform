using Aip.Ai;
using Aip.Analysis;
using Aip.Engines.Roslyn;
using Aip.Engines.TypeScript;
using Aip.Infrastructure;
using Aip.Infrastructure.AzureBlob;
using Aip.Knowledge;
using Aip.Observability;
using Aip.Plugins.Angular;
using Aip.Plugins.AspNetCore;
using Aip.Plugins.NextJs;
using Aip.Plugins.React;
using Aip.Projections;
using Aip.Query;
using Aip.Registries;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aip.Host;

/// <summary>
/// The single composition root. Every module contributes its own registrations; the host only
/// composes them. This is the one place concrete implementations are wired — everything else
/// depends on abstractions (Clean Architecture, dependency rule inward).
/// </summary>
public static class PlatformComposition
{
    public static IServiceCollection AddApplicationIntelligencePlatform(this IServiceCollection services, IConfiguration configuration)
    {
        // Guarantees ILogger<T> always resolves for every constructor that asks for one, even when the
        // real Serilog pipeline (AddAipLogging, called separately by the real entry points) hasn't been
        // wired up — e.g. in tests, which compose this same method without ever calling AddAipLogging.
        // Calling AddLogging() more than once (Serilog's own AddAipLogging call included) is safe; the
        // underlying registrations use TryAdd semantics.
        services.AddLogging();

        // Infrastructure adapters (behind the Core/Platform ports)
        services.AddAipInfrastructure(configuration);

        // Document store: Azure Blob when configured (the standalone/production, multi-repo scenario) — overrides the
        // filesystem default AddAipInfrastructure() just registered. Neither AIP's own repo nor any
        // analyzed repo is ever written to; docs live in one central, durable location either way.
        // AIP_BLOB_CONNECTION_STRING (env var) always wins over appsettings — the production path.
        string? blobConnection = Environment.GetEnvironmentVariable("AIP_BLOB_CONNECTION_STRING") ?? configuration["Storage:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(blobConnection))
        {
            string container = Environment.GetEnvironmentVariable("AIP_BLOB_CONTAINER") ?? configuration["Storage:Container"] ?? "documents";
            services.AddAipAzureBlobDocumentStore(blobConnection, container);
        }

        // Control plane
        services.AddAipRegistries();

        // Cross-cutting
        services.AddAipObservability();

        // Write path: analysis → validation/merge → relationship resolution
        services.AddAipAnalysis();
        services.AddAipKnowledge();

        // Read path
        services.AddAipProjections();
        services.AddAipQuery();

        // AI platform (dual-use: write-path probabilistic analyzers + read-path rendering)
        services.AddAipAi();

        // Language engines
        services.AddAipRoslynEngine();
        services.AddAipTypeScriptEngine();

        // Technology plugins
        services.AddAipAspNetCorePlugin();
        services.AddAipAngularPlugin();
        services.AddAipReactPlugin();
        services.AddAipNextPlugin();

        return services;
    }
}
