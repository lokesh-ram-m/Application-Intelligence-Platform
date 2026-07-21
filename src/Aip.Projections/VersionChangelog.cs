using Aip.Abstractions.Ai;
using Aip.Abstractions.History;
using Aip.Abstractions.Projections;
using Aip.Core.Abstractions;
using Aip.Core.Domain;

namespace Aip.Projections;

/// <summary>
/// Turns a <see cref="SnapshotDiff"/> into a short changelog — a deterministic structured summary always
/// computed first (cheap, always accurate, available with AI off), then rewritten into prose by the AI
/// when available. Falls back to the deterministic summary itself, with a recorded
/// <see cref="AiFallbackEvent"/>, on any AI failure — the exact same resilience pattern
/// <see cref="DocumentationProjection"/> already uses for pages.
/// </summary>
internal sealed class VersionChangelogGenerator : IVersionChangelogGenerator
{
    private const int MaxNamesPerList = 15;

    private readonly IAiPlatform _ai;
    private readonly IContextBuilder _context;
    private readonly IAiFallbackStore _fallback;

    public VersionChangelogGenerator(IAiPlatform ai, IContextBuilder context, IAiFallbackStore fallback)
    {
        _ai = ai;
        _context = context;
        _fallback = fallback;
    }

    public async Task<(string Summary, bool AiWritten)> GenerateAsync(
        string application, IReadOnlyList<string> repositories, SnapshotDiff diff, CancellationToken ct = default)
    {
        string deterministic = BuildDeterministicSummary(diff, application);
        if (_ai.IsAvailable)
        {
            try
            {
                var values = new Dictionary<string, string> { ["app"] = application, ["model"] = _context.Build(deterministic) };

                return (await _ai.RenderAsync("version-changelog", values, ct), true);
            }
            catch (Exception ex)
            {
                await _fallback.RecordAsync(new AiFallbackEvent(
                    application, repositories, "version-changelog", AiFallbackClassification.Classify(ex), ex.Message, DateTimeOffset.UtcNow), ct);
            }
        }

        return (deterministic, false);
    }

    private static string BuildDeterministicSummary(SnapshotDiff diff, string application)
    {
        IReadOnlyList<DiffGrouping.ApplicationImpact> byApp = DiffGrouping.GroupByOwningApplication(diff);

        // A leaf application's diff only ever touches its own nodes/relationships, so this reproduces
        // today's flat output byte-for-byte. The per-app breakdown appears whenever this publish actually
        // pulled in a CHILD's changes — not merely "more than one owner appears," since a composite whose
        // diff this run only touches one child also produces exactly one group (see IsCompositeImpact).
        if (!DiffGrouping.IsCompositeImpact(diff, application))
        {
            var flat = new List<string>();
            AppendSection(flat, "Nodes added", diff.AddedNodes.Select(NodeLabel));
            AppendSection(flat, "Nodes removed", diff.RemovedNodes.Select(NodeLabel));
            AppendSection(flat, "Relationships added", diff.AddedRelationships.Select(RelationshipLabel));
            AppendSection(flat, "Relationships removed", diff.RemovedRelationships.Select(RelationshipLabel));

            return flat.Count == 0 ? "No changes." : string.Join("\n", flat);
        }

        var lines = new List<string>();
        foreach (DiffGrouping.ApplicationImpact impact in byApp)
        {
            lines.Add($"## {impact.Application}");
            AppendSection(lines, "Nodes added", impact.AddedNodes.Select(NodeLabel));
            AppendSection(lines, "Nodes removed", impact.RemovedNodes.Select(NodeLabel));
            AppendSection(lines, "Relationships added", impact.AddedRelationships.Select(RelationshipLabel));
            AppendSection(lines, "Relationships removed", impact.RemovedRelationships.Select(RelationshipLabel));
        }

        (IReadOnlyList<Relationship> addedIntegrations, IReadOnlyList<Relationship> removedIntegrations) = DiffGrouping.CrossApplicationRelationships(diff);
        if (addedIntegrations.Count > 0 || removedIntegrations.Count > 0)
        {
            lines.Add("## Integrations (relationships between sub-applications)");
            AppendSection(lines, "New integrations", addedIntegrations.Select(RelationshipLabel));
            AppendSection(lines, "Removed integrations", removedIntegrations.Select(RelationshipLabel));
        }

        return lines.Count == 0 ? "No changes." : string.Join("\n", lines);
    }

    private static void AppendSection(List<string> lines, string heading, IEnumerable<string> items)
    {
        List<string> list = items.ToList();
        if (list.Count == 0) return;

        lines.Add($"{heading} ({list.Count}):");
        foreach (string item in list.Take(MaxNamesPerList)) lines.Add($"- {item}");
        if (list.Count > MaxNamesPerList) lines.Add($"- …and {list.Count - MaxNamesPerList} more");
    }

    private static string NodeLabel(KnowledgeNode n) => n.Label();

    private static string RelationshipLabel(Relationship r) => r.Label();
}
