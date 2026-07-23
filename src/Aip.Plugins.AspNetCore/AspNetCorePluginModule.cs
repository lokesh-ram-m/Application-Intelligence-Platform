using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Engines.Roslyn;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Plugins.AspNetCore;

/// <summary>
/// The ASP.NET Core technology plugin. Consumes the Roslyn engine and runs a pipeline of deterministic
/// analyzers, each emitting immutable Discoveries. It writes nothing to the Knowledge Model.
/// </summary>
internal sealed class AspNetCorePlugin : IPlugin
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = new IAnalyzer[]
    {
        new ControllerAnalyzer(),
        new AzureFunctionAnalyzer(),
        new ServiceAnalyzer(),
        new RepositoryAnalyzer(),
        new InterfaceAnalyzer(),
        new EntityAnalyzer(),
        new AuditLogAnalyzer(),
        new StatusWorkflowAnalyzer(),
        new BusinessRuleAnalyzer(),
        new DbContextAnalyzer(),
        new MigrationAnalyzer(),
        new DatabaseOperationAnalyzer(),
        new DependencyInjectionAnalyzer(),
        new DependencyAnalyzer(),
        new PackageAnalyzer(),
        new InfrastructureAnalyzer(),
        new AuthorizationPolicyAnalyzer(),
        new OutboundHttpAnalyzer(),
        new CqrsAnalyzer(),
        new MediatorPublishAnalyzer(),
        new ValidatorAnalyzer(),
        new MessagingAnalyzer(),
        new DataAccessAnalyzer(),
        new FilterAnalyzer(),
        new TechnologyUsageAnalyzer(),
        new ProgramAnalyzer(),
        new MinimalApiAnalyzer(),
        new ConfigurationAnalyzer(),
        new ConfigurationUsageAnalyzer(),
    };

    public PluginManifest Manifest { get; } = new(
        Id: "aip.plugins.aspnetcore",
        Version: "0.1.0",
        SupportedArtifacts: new[] { "dotnet-project" },
        Languages: new[] { "csharp" },
        Capabilities: new[]
        {
            "Controller", "Endpoint", "AzureFunction", "Service", "Repository", "Interface", "Entity", "AuditLog", "StatusWorkflow", "BusinessRule", "DataStore", "Configuration",
            "Project", "Technology", "Cache", "AuthScheme", "Cors", "HealthCheck", "Logging", "Messaging",
            "BackgroundJob", "Middleware", "Authorization", "Command", "Query", "Event", "Handler", "Validator",
            "MessageBroker", "Consumer", "Message", "DataAccess", "Filter", "Resilience", "Component", "DatabaseOperation", "Migration",
            "AuthorizationPolicy", "OutboundCall",
        },
        Priority: 100,
        Dependencies: Array.Empty<string>());

    public async Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        if (context.Model is not RoslynSemanticModel)
        {
            sink.Report(Aip.Core.Domain.Diagnostic.Warning(
                $"Expected a Roslyn model for '{context.Artifact.Name}' but got '{context.Model.Parser}'.", Manifest.Id));

            return;
        }

        foreach (IAnalyzer analyzer in _analyzers)
        {
            ct.ThrowIfCancellationRequested();
            await analyzer.AnalyzeAsync(context, sink, ct);
        }
    }
}

public static class AspNetCorePluginModule
{
    public static IServiceCollection AddAipAspNetCorePlugin(this IServiceCollection services)
    {
        services.AddSingleton<IPlugin, AspNetCorePlugin>();

        return services;
    }
}
