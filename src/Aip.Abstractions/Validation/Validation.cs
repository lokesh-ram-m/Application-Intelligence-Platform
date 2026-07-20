using Aip.Core.Domain;

namespace Aip.Abstractions.Validation;

/// <summary>The accepted output of validation: canonical nodes/relationships plus any diagnostics.</summary>
public sealed record ValidationResult(
    IReadOnlyList<KnowledgeNode> Nodes,
    IReadOnlyList<Relationship> Relationships,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The single writer's gate. Schema-validates, normalizes identity, de-duplicates, resolves conflicts,
/// aggregates evidence and computes confidence — turning Discoveries into accepted Knowledge. The same
/// governance applies to deterministic and probabilistic (and resolver) Discoveries alike.
/// </summary>
public interface IValidationPipeline
{
    /// <param name="knownNodeIdentities">
    /// Identities already committed (e.g. the current snapshot) that relationship endpoints may
    /// reference — used when validating resolver-emitted relationships against existing Knowledge.
    /// </param>
    Task<ValidationResult> ValidateAsync(
        IReadOnlyList<Discovery> discoveries,
        IReadOnlyCollection<KnowledgeIdentity>? knownNodeIdentities = null,
        CancellationToken ct = default);
}
