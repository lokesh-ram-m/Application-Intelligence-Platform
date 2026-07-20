using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Engines.TypeScript;
using Aip.Plugins.React;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Plugins.NextJs;

/// <summary>
/// The Next.js technology plugin. Next is built on React, so it reuses every React analyzer — components,
/// hooks, fetch/axios calls, context, roles, data grids, filters, import/export, form fields, and
/// component composition all operate on plain JSX/TSX text or the TypeScript semantic model with no
/// React-CRA-specific assumption, so they apply unchanged to Next's .tsx files — and adds Next-specific
/// file-system routing (App Router + Pages Router, including API routes). Claims Next workspaces (a
/// directory with a next.config.*).
/// </summary>
internal sealed class NextPlugin : IPlugin
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = new IAnalyzer[]
    {
        new ReactComponentAnalyzer(),
        new ReactHookAnalyzer(),
        new ReactApiCallAnalyzer(),
        new ReactContextAnalyzer(),
        new ReactRoleAnalyzer(),
        new ReactDataGridAnalyzer(),
        new ReactFilterAnalyzer(),
        new ReactImportExportAnalyzer(),
        new ReactFormFieldAnalyzer(),
        new ReactCompositionAnalyzer(),
        new NextRouteAnalyzer(),
        new FrontendAuthAnalyzer(),
    };

    public PluginManifest Manifest { get; } = new(
        Id: "aip.plugins.nextjs",
        Version: "0.1.0",
        SupportedArtifacts: new[] { "nextjs-workspace" },
        Languages: new[] { "typescript" },
        Capabilities: new[] { "UIComponent", "Hook", "ApiCall", "Context", "Route", "Role", "DataGrid", "Filter", "ImportExport", "FormField", "AuthProvider", "TokenStorage", "TokenAttachment" },
        Priority: 95,
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

public static class NextPluginModule
{
    public static IServiceCollection AddAipNextPlugin(this IServiceCollection services)
    {
        services.AddSingleton<IPlugin, NextPlugin>();

        return services;
    }
}
