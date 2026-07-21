namespace Aip.Abstractions.Documents;

/// <summary>
/// Stores generated documentation content by application + relative path — the durable, servable location
/// a Document Viewer reads from. Writing here never touches a source-controlled repository (neither AIP's
/// own nor any analyzed repo), so no repo grows as more applications are documented.
///
/// Implementations: a local filesystem store (default, for local/dev use) and Azure Blob Storage (for the
/// standalone/production, multi-repo scenario). The two are selected via configuration; callers depend
/// only on this port, so Document Creator and Document Viewer never know — or need to agree on — which is active.
///
/// The store holds every published documentation <b>version</b> for an application, side by side (a
/// caller-side path convention — <c>{app}/v{N}/{relativePath}</c> — layered on top of this interface, not a
/// change to it; see <see cref="DocumentVersionsIndex"/>). Historical/versioned Knowledge Model facts still
/// belong to the Knowledge Store, not here — the two are deliberately separate: this is a KV-shaped content
/// store, that is an identity-keyed, diffable graph store.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Writes (creates or overwrites) one document.</summary>
    Task WriteAsync(string application, string relativePath, string content, string contentType = "text/markdown", CancellationToken ct = default);

    /// <summary>Reads one document, or null if it does not exist.</summary>
    Task<string?> ReadAsync(string application, string relativePath, CancellationToken ct = default);

    /// <summary>Lists every document's relative path currently stored for an application.</summary>
    Task<IReadOnlyList<string>> ListAsync(string application, CancellationToken ct = default);

    /// <summary>
    /// Removes every document currently stored for an application. Callers publishing a fresh, complete
    /// set of pages should call this first, so a regenerate never leaves stale/orphaned pages behind
    /// (e.g. a page for a capability that no longer exists in the current Knowledge Model).
    /// </summary>
    Task ClearApplicationAsync(string application, CancellationToken ct = default);
}

/// <summary>One page's position in an application's documentation, for reconstructing sidebar order.
/// <paramref name="AiWritten"/> is whether AI actually rendered this page's prose (vs. a deterministic
/// fallback or a page that's always deterministic by design) — the Viewer uses it to show its AI-content
/// notice only on pages it's actually true for.</summary>
public sealed record DocumentManifestEntry(string Path, int Order, bool AiWritten = false);

/// <summary>
/// The page order for an application, written to the store as an ordinary document (just JSON) alongside
/// the pages themselves. A reader (e.g. a site builder) fetches this first so it can lay pages out in the
/// order they were generated in, without needing any information beyond what the store holds.
/// </summary>
public sealed record DocumentManifest(IReadOnlyList<DocumentManifestEntry> Pages)
{
    /// <summary>The reserved relative path the manifest is stored under — excluded from page listings.</summary>
    public const string FileName = "_manifest.json";
}

/// <summary>One repository's commit that fed a particular documentation version.</summary>
public sealed record VersionedRepositoryCommit(string RepositoryName, string CommitSha);

/// <summary>One published documentation version for an application — pages live under the store at
/// the <c>v{Number}/</c> prefix.</summary>
public sealed record DocumentVersionEntry(int Number, DateTimeOffset CreatedAt, IReadOnlyList<VersionedRepositoryCommit> Repositories, int PagesGenerated);

/// <summary>
/// Every documentation version ever published for an application, oldest first — never mutated, only
/// appended to, the same append-only spirit as the Knowledge Store's Snapshots (though this and the
/// Knowledge Store remain deliberately separate stores). "Latest" is always <c>Versions.Max(v =&gt; v.Number)</c>,
/// never stored redundantly, so it can never drift out of sync with the versions actually present.
/// </summary>
public sealed record DocumentVersionsIndex(IReadOnlyList<DocumentVersionEntry> Versions)
{
    /// <summary>The reserved relative path the version index is stored under, at the application root.</summary>
    public const string FileName = "_versions.json";
}

/// <summary>One application known to the document store — enough for a landing page to link to it.
/// <paramref name="Children"/> names sub-applications this one covers (empty for a leaf application) —
/// lets the landing page group composites and their children instead of listing every app flat.</summary>
public sealed record ApplicationIndexEntry(string Name, string Slug, IReadOnlyList<string>? Children = null)
{
    public IReadOnlyList<string> Children { get; init; } = Children ?? Array.Empty<string>();
}

/// <summary>
/// Every application that currently has documentation in the store. Written to a reserved pseudo-
/// application ("_index") so it round-trips through the ordinary <see cref="IDocumentStore"/> methods —
/// no interface change needed. A landing page reads this instead of having to enumerate the whole store.
/// </summary>
public sealed record ApplicationsIndex(IReadOnlyList<ApplicationIndexEntry> Applications)
{
    /// <summary>The reserved pseudo-application name the index is stored under.</summary>
    public const string IndexApplication = "_index";

    /// <summary>The reserved relative path the index is stored at.</summary>
    public const string FileName = "_applications.json";
}

/// <summary>
/// Shared path/name normalization for document stores, so every implementation treats "application" and
/// "relativePath" identically — no silent divergence between filesystem path rules and blob-name rules.
/// </summary>
public static class DocumentPaths
{
    /// <summary>Deterministic, filesystem- and blob-safe slug for an application name.</summary>
    public static string SlugifyApplication(string application)
    {
        if (string.IsNullOrWhiteSpace(application))
            throw new ArgumentException("application must not be empty.", nameof(application));

        return new string(application.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    }

    /// <summary>Normalizes a relative path to forward slashes with no leading slash, and rejects path traversal.</summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("relativePath must not be empty.", nameof(relativePath));

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new ArgumentException($"relativePath must not contain '.' or '..' segments: '{relativePath}'.", nameof(relativePath));

        return string.Join('/', segments);
    }
}
