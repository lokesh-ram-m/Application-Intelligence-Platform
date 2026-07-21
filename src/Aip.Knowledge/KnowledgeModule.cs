using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Validation;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Knowledge;

public static class KnowledgeModule
{
    public static IServiceCollection AddAipKnowledge(this IServiceCollection services)
    {
        services.AddSingleton<IValidationPipeline, ValidationPipeline>();

        // Relationship resolvers (deterministic) + the engine that hosts them.
        services.AddSingleton<IRelationshipResolver, ApiCallToEndpointResolver>();
        services.AddSingleton<IRelationshipResolver, ServiceToDatabaseResolver>();
        services.AddSingleton<IRelationshipResolver, PublisherSubscriberResolver>();
        services.AddSingleton<IRelationshipResolver, AuditLogToEntityResolver>();
        services.AddSingleton<IRelationshipResolver, CrossRepositoryDependencyResolver>();
        services.AddSingleton<IRelationshipResolutionEngine, RelationshipResolutionEngine>();

        return services;
    }
}
