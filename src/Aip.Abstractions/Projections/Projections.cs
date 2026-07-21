using Aip.Core.Domain;

namespace Aip.Abstractions.Projections;

/// <summary><paramref name="Children"/> names the sub-applications folded into this Snapshot (empty for a
/// leaf application) — lets a projection mention what a composite application actually covers.
/// <paramref name="Notes"/> is human-authored project documentation (README.md/CLAUDE.md), null when none
/// was found — a lower-trust input the product-specification pages may cross-reference against the
/// Knowledge Model, never a source of verified facts on its own.</summary>
public sealed record ProjectionRequest(Snapshot Snapshot, IReadOnlyList<string> Repositories, IReadOnlyList<string>? Children = null, string? Notes = null)
{
    public IReadOnlyList<string> Children { get; init; } = Children ?? Array.Empty<string>();
}

/// <summary>A single rendered output artifact of a projection (a markdown file, a JSON document, etc.).
/// <paramref name="AiWritten"/> is whether AI actually produced <paramref name="Content"/> (as opposed to
/// a deterministic render — either by design, or because an AI attempt fell back) — carried through to the
/// document manifest so a reader can tell which pages are AI narrative.</summary>
public sealed record ProjectionArtifact(string Name, string ContentType, string Content, int Order = 0, bool AiWritten = false);

public sealed record ProjectionResult(string ProjectionName, IReadOnlyList<ProjectionArtifact> Artifacts);

/// <summary>
/// A projection turns a Snapshot into one output (documentation, API catalog, …). Documentation is one
/// projection among many. Projections read the Knowledge Model only — never source code.
/// </summary>
public interface IProjection
{
    string Name { get; }
    Task<ProjectionResult> ProjectAsync(ProjectionRequest request, CancellationToken ct = default);
}

/// <summary>Runs the registered projections against a snapshot; never coupled to any single output.
/// <paramref name="repositories"/> is every repository backing the application (for context that a
/// Snapshot alone doesn't carry — e.g. AI fallback attribution); omit it where repo identity doesn't
/// matter to the caller.</summary>
public interface IProjectionEngine
{
    Task<IReadOnlyList<ProjectionResult>> RunAsync(Snapshot snapshot, IReadOnlyList<string>? repositories = null,
        IReadOnlyList<string>? children = null, string? notes = null, CancellationToken ct = default);
}

/// <summary>
/// Turns a version-to-version Knowledge Model diff into a short, human-readable changelog — AI-narrated
/// when available, falling back to a deterministic structured summary (see the concrete implementation in
/// Aip.Projections) on failure or when no AI provider is configured. The same kind of "Knowledge Model
/// facts → AI-assisted prose" concern <see cref="IProjection"/> covers for documentation pages, just for a
/// different artifact shape (one short summary, not a page set).
/// </summary>
public interface IVersionChangelogGenerator
{
    Task<(string Summary, bool AiWritten)> GenerateAsync(
        string application, IReadOnlyList<string> repositories, SnapshotDiff diff, CancellationToken ct = default);
}
