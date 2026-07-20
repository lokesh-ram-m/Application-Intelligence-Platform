namespace Aip.Abstractions.History;

/// <summary>One occurrence of documentation prose falling back to deterministic rendering because the AI
/// call failed or was unavailable. <paramref name="Repositories"/> is every repository backing the
/// application at the time (an app can span more than one), since AI-call context today doesn't carry a
/// single "current" repository. <paramref name="Section"/> is the prompt template name (e.g.
/// "product-overview", "tech-frontend-roles") — the most specific "where" identifier available.</summary>
public sealed record AiFallbackEvent(
    string Application,
    IReadOnlyList<string> Repositories,
    string Section,
    string Reason,
    string? Detail,
    DateTimeOffset OccurredAt);

/// <summary>Durable record of every AI fallback, so "how often is AI actually failing, and for which
/// repo" is answerable from data instead of scrollback. Separate from Run History (which records whole-run
/// outcomes) — a single run can fall back on some pages and not others.</summary>
public interface IAiFallbackStore
{
    Task RecordAsync(AiFallbackEvent evt, CancellationToken ct = default);
}
