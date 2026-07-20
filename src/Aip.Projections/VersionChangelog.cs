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
        string deterministic = BuildDeterministicSummary(diff);
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

    private static string BuildDeterministicSummary(SnapshotDiff diff)
    {
        var lines = new List<string>();
        AppendSection(lines, "Nodes added", diff.AddedNodes.Select(NodeLabel));
        AppendSection(lines, "Nodes removed", diff.RemovedNodes.Select(NodeLabel));
        AppendSection(lines, "Relationships added", diff.AddedRelationships.Select(RelationshipLabel));
        AppendSection(lines, "Relationships removed", diff.RemovedRelationships.Select(RelationshipLabel));

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
