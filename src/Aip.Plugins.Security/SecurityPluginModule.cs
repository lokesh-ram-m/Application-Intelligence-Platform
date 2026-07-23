using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Plugins.Security;

/// <summary>
/// The Security plugin — cross-cutting rather than tied to one language or framework, unlike every other
/// technology plugin. Claims the plaintext config/pipeline files <c>RepositoryScanner</c> discovers
/// (<c>security-scan-target</c>) and runs text-pattern analyzers over them. Currently just secret
/// scanning; the natural place for future generic security checks (permissive CORS already flags via the
/// existing Cors node's <c>origins</c> property, consumed on the Security page alongside this).
/// </summary>
internal sealed class SecurityPlugin : IPlugin
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = new IAnalyzer[]
    {
        new SecretScanAnalyzer(),
        new DependencyVulnerabilityAnalyzer(),
    };

    // Claims every workspace kind other plugins already claim too (a .csproj, a package.json-rooted
    // frontend workspace) — multiple plugins can independently claim the same artifact (IPluginHost.
    // SelectFor returns a list), so this runs alongside the AspNetCore/React/Angular/NextJs plugins on
    // those same artifacts rather than needing its own separate discovery pass.
    public PluginManifest Manifest { get; } = new(
        Id: "aip.plugins.security",
        Version: "0.1.0",
        SupportedArtifacts: new[] { "security-scan-target", "dotnet-project", "react-workspace", "angular-workspace", "nextjs-workspace" },
        Languages: new[] { "plaintext" },
        Capabilities: new[] { "Vulnerability" },
        Priority: 50,
        Dependencies: Array.Empty<string>());

    public async Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        foreach (IAnalyzer analyzer in _analyzers)
        {
            ct.ThrowIfCancellationRequested();
            await analyzer.AnalyzeAsync(context, sink, ct);
        }
    }
}

public static class SecurityPluginModule
{
    public static IServiceCollection AddAipSecurityPlugin(this IServiceCollection services)
    {
        services.AddSingleton<IPlugin, SecurityPlugin>();
        services.AddSingleton<ILanguageEngine, PlainTextLanguageEngine>();

        return services;
    }
}
