using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Engines.TypeScript;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Plugins.Angular;

/// <summary>
/// The Angular technology plugin. Consumes the TypeScript engine and runs analyzers for components,
/// routes and HTTP clients. It emits immutable Discoveries only. Declares a dependency on the ASP.NET
/// Core plugin so it is ordered after backend endpoints exist (the frontend→backend link is resolved
/// later by the Relationship Resolution Engine).
/// </summary>
internal sealed class AngularPlugin : IPlugin
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = new IAnalyzer[]
    {
        new AngularComponentAnalyzer(),
        new AngularTemplateCompositionAnalyzer(),
        new AngularServiceAnalyzer(),
        new AngularGuardAnalyzer(),
        new AngularRouteAnalyzer(),
        new AngularDependencyAnalyzer(),
        new HttpClientAnalyzer(),
        new FrontendAuthAnalyzer(),
    };

    public PluginManifest Manifest { get; } = new(
        Id: "aip.plugins.angular",
        Version: "0.1.0",
        SupportedArtifacts: new[] { "angular-workspace" },
        Languages: new[] { "typescript" },
        Capabilities: new[] { "UIComponent", "UIService", "Guard", "Interceptor", "Route", "ApiCall", "AuthProvider", "TokenStorage", "TokenAttachment" },
        Priority: 90,
        Dependencies: new[] { "aip.plugins.aspnetcore" });

    public async Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        if (context.Model is not TypeScriptSemanticModel)
        {
            sink.Report(Aip.Core.Domain.Diagnostic.Warning(
                $"Expected a TypeScript model for '{context.Artifact.Name}' but got '{context.Model.Parser}'.", Manifest.Id));

            return;
        }

        foreach (IAnalyzer analyzer in _analyzers)
        {
            ct.ThrowIfCancellationRequested();
            await analyzer.AnalyzeAsync(context, sink, ct);
        }
    }
}

public static class AngularPluginModule
{
    public static IServiceCollection AddAipAngularPlugin(this IServiceCollection services)
    {
        services.AddSingleton<IPlugin, AngularPlugin>();

        return services;
    }
}
