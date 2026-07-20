using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Engines.TypeScript;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Plugins.React;

/// <summary>
/// The React technology plugin. Consumes the TypeScript engine and runs heuristic analyzers for
/// components (props, hooks, client/server), custom hooks, fetch/axios/wrapped-client API calls,
/// context/stores, routes (with role gating), the role vocabulary, data grids and their columns
/// (TanStack or plain HTML tables), client-side filters, import/export capabilities, form fields with
/// their validation, and component composition (which child components a page/component renders, from
/// React's own PascalCase-JSX-tag convention). It emits immutable Discoveries only; the frontend→backend
/// link is resolved later by the Relationship Resolution Engine. Claims plain React workspaces (a
/// package.json with react but not next/angular).
/// </summary>
internal sealed class ReactPlugin : IPlugin
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = new IAnalyzer[]
    {
        new ReactComponentAnalyzer(),
        new ReactHookAnalyzer(),
        new ReactApiCallAnalyzer(),
        new ReactContextAnalyzer(),
        new ReactRouteAnalyzer(),
        new ReactRoleAnalyzer(),
        new ReactDataGridAnalyzer(),
        new ReactFilterAnalyzer(),
        new ReactImportExportAnalyzer(),
        new ReactFormFieldAnalyzer(),
        new ReactCompositionAnalyzer(),
        new FrontendAuthAnalyzer(),
    };

    public PluginManifest Manifest { get; } = new(
        Id: "aip.plugins.react",
        Version: "0.1.0",
        SupportedArtifacts: new[] { "react-workspace" },
        Languages: new[] { "typescript" },
        Capabilities: new[] { "UIComponent", "Hook", "ApiCall", "Context", "Route", "Role", "DataGrid", "Filter", "ImportExport", "FormField", "AuthProvider", "TokenStorage", "TokenAttachment" },
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

public static class ReactPluginModule
{
    public static IServiceCollection AddAipReactPlugin(this IServiceCollection services)
    {
        services.AddSingleton<IPlugin, ReactPlugin>();

        return services;
    }
}
