using System.Text;
using System.Text.Json;

using Aip.Abstractions.Documents;
using Aip.Abstractions.History;
using Aip.Infrastructure;
using Aip.Viewer.Views;

using Markdig;

namespace Aip.Viewer;

// Everything a route handler needs to go from "request" to "rendered HTML" — reads from IDocumentStore
// / IVersionChangeStore, then hands the assembled data to the Views layer for markup. No HTML lives here.
internal static class DocumentEndpoints
{
    internal static async Task<IResult> RenderPageAsync(IDocumentStore store, IVersionChangeStore changes, string application, int version, string path,
        JsonSerializerOptions jsonOptions, MarkdownPipeline markdownPipeline)
    {
        string relativePath = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path : path + ".md";
        string? markdown = await store.ReadAsync(application, $"v{version}/{relativePath}");
        if (markdown is null)
            return Results.Content(NotFoundPage.Render("The page you're looking for may have moved, been renamed, or belongs to a version that's no longer available."), "text/html", statusCode: StatusCodes.Status404NotFound);

        DocumentVersionsIndex versions = await ReadVersionsIndexAsync(store, application, jsonOptions);
        string navHtml = await BuildNavAsync(store, application, version, relativePath, jsonOptions);
        (string versionPickerHtml, string? changesUrl) = await BuildVersionPickerAsync(changes, application, version, versions, relativePath);
        string bodyHtml = Markdown.ToHtml(markdown, markdownPipeline);
        bool aiWritten = await IsAiWrittenAsync(store, application, version, relativePath, jsonOptions);
        IReadOnlyList<string> children = await ReadApplicationChildrenAsync(store, application, jsonOptions);

        return Results.Content(DocumentPage.Render(application, relativePath, navHtml, versionPickerHtml, bodyHtml, aiWritten, changesUrl, children), "text/html");
    }

    internal static async Task<IResult> RenderChangesAsync(string application, int version, IVersionChangeStore changes, MarkdownPipeline markdownPipeline)
    {
        DocumentVersionChange? change = await changes.GetAsync(application, version);

        return change is null
            ? Results.Content(NotFoundPage.Render("There's no change record for this version — it may be the first documented version, or nothing changed here."), "text/html", statusCode: StatusCodes.Status404NotFound)
            : Results.Content(ChangesPage.Render(application, version, change, markdownPipeline), "text/html");
    }

    internal static async Task<IResult> RedirectToLatestAsync(string application, string path, IDocumentStore store, JsonSerializerOptions jsonOptions)
    {
        DocumentVersionsIndex versions = await ReadVersionsIndexAsync(store, application, jsonOptions);
        if (versions.Versions.Count == 0)
            return Results.Content(NotFoundPage.Render($"There's no documentation for \"{application}\" yet."), "text/html", statusCode: StatusCodes.Status404NotFound);
        int latest = versions.Versions.Max(v => v.Number);
        // A bare "/{application}" (no page path at all) is a reasonable URL for a user to type or land on
        // directly — send it straight to the overview page rather than an empty trailing segment, which
        // would otherwise redirect once more into a 404 (no document is ever stored at just "v{N}/.md").
        string target = string.IsNullOrEmpty(path) ? "product-specification/overview" : path;

        // 302 (not 301) because "latest" changes over time; a cached permanent redirect would go stale.
        return Results.Redirect($"/{application}/v{latest}/{target}");
    }

    internal static async Task<IResult> RenderLandingAsync(IDocumentStore store, JsonSerializerOptions jsonOptions)
    {
        string? indexJson = await store.ReadAsync(ApplicationsIndex.IndexApplication, ApplicationsIndex.FileName);
        List<ApplicationIndexEntry> apps = new();
        if (indexJson is not null)
        {
            try { apps = JsonSerializer.Deserialize<ApplicationsIndex>(indexJson, jsonOptions)?.Applications.ToList() ?? new(); }
            catch (JsonException) { /* treat a corrupt index as empty */ }
        }

        var sb = new StringBuilder();
        if (apps.Count == 0)
        {
            sb.Append("""
                <div class="empty-state">
                  <p>No applications documented yet.</p>
                </div>
                """);
        }
        else
        {
            // A leaf application that's covered by some composite doesn't get its own top-level card — it's
            // still fully reachable (the composite's card links to it, and its own URL never changes), just
            // not cluttering the grid alongside the composite that already documents it.
            var childNames = apps.SelectMany(a => a.Children).ToHashSet();
            Dictionary<string, ApplicationIndexEntry> byName = apps.ToDictionary(a => a.Name);

            sb.Append("<div class=\"app-grid\">");
            foreach (ApplicationIndexEntry a in apps.Where(a => !childNames.Contains(a.Name)).OrderBy(a => a.Name))
            {
                // .app-card is a <div>, not an <a> — a composite card needs its own links to each child
                // alongside the card's main link, and an <a> cannot legally nest another <a> inside it.
                string childrenHtml = a.Children.Count == 0 ? "" : $"""
                    <div class="app-card-children">Covers: {string.Join(", ", a.Children.Select(c =>
                        byName.TryGetValue(c, out ApplicationIndexEntry? entry) ? $"<a href=\"/{entry.Slug}/product-specification/overview\">{c}</a>" : c))}</div>
                    """;
                sb.Append($"""
                    <div class="app-card">
                      <a class="app-card-link-area" href="/{a.Slug}/product-specification/overview">
                        <span class="app-card-name">{a.Name}</span>
                        <span class="app-card-link">View documentation &rarr;</span>
                      </a>
                      {childrenHtml}
                    </div>
                    """);
            }
            sb.Append("</div>");
        }

        return Results.Content(LandingPage.Render(sb.ToString()), "text/html");
    }

    // The manifest is per-page provenance (see DocumentManifestEntry.AiWritten) — read fresh on every
    // request, same as everything else here, so the badge only ever shows on the exact page it's true for
    // rather than unconditionally on all of them.
    private static async Task<bool> IsAiWrittenAsync(IDocumentStore store, string application, int version, string relativePath, JsonSerializerOptions jsonOptions)
    {
        string? manifestJson = await store.ReadAsync(application, $"v{version}/{DocumentManifest.FileName}");
        if (manifestJson is null) return false;
        try
        {
            DocumentManifest? manifest = JsonSerializer.Deserialize<DocumentManifest>(manifestJson, jsonOptions);

            return manifest?.Pages.FirstOrDefault(p => p.Path == relativePath)?.AiWritten ?? false;
        }
        catch (JsonException) { return false; }
    }

    // Reads the same shared ApplicationsIndex RenderLandingAsync reads, just to find one entry's Children —
    // small and infrequent enough (once per page render) that a dedicated index isn't worth it.
    private static async Task<IReadOnlyList<string>> ReadApplicationChildrenAsync(IDocumentStore store, string application, JsonSerializerOptions jsonOptions)
    {
        string? indexJson = await store.ReadAsync(ApplicationsIndex.IndexApplication, ApplicationsIndex.FileName);
        if (indexJson is null) return Array.Empty<string>();
        try
        {
            ApplicationsIndex? index = JsonSerializer.Deserialize<ApplicationsIndex>(indexJson, jsonOptions);

            return index?.Applications.FirstOrDefault(a => a.Name == application)?.Children ?? Array.Empty<string>();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static async Task<DocumentVersionsIndex> ReadVersionsIndexAsync(IDocumentStore store, string application, JsonSerializerOptions jsonOptions)
    {
        string? json = await store.ReadAsync(application, DocumentVersionsIndex.FileName);
        if (json is null) return new DocumentVersionsIndex(new List<DocumentVersionEntry>());
        try { return JsonSerializer.Deserialize<DocumentVersionsIndex>(json, jsonOptions) ?? new DocumentVersionsIndex(new List<DocumentVersionEntry>()); }
        catch (JsonException) { return new DocumentVersionsIndex(new List<DocumentVersionEntry>()); }
    }

    private static async Task<(string PickerHtml, string? ChangesUrl)> BuildVersionPickerAsync(IVersionChangeStore changes, string application, int currentVersion, DocumentVersionsIndex versions, string relativePath)
    {
        if (versions.Versions.Count == 0) return ("", null);
        int latest = versions.Versions.Max(v => v.Number);

        var sb = new StringBuilder("<select class=\"version-picker\" onchange=\"location.href=this.value\">");
        foreach (DocumentVersionEntry v in versions.Versions.OrderByDescending(v => v.Number))
        {
            string selected = v.Number == currentVersion ? " selected" : "";
            sb.Append($"<option value=\"/{application}/v{v.Number}/{relativePath}\"{selected}>v{v.Number}</option>");
        }
        sb.Append("</select>");

        string? changesUrl = null;
        DocumentVersionEntry? current = versions.Versions.FirstOrDefault(v => v.Number == currentVersion);
        if (current is not null)
        {
            sb.Append(BuildVersionDetails(current, current.Number == latest));

            // "What changed" only makes sense once there's a predecessor to compare against — never for v1,
            // and only when a change record actually exists (a publish that hit the empty-diff skip has none).
            if (current.Number > 1 && await changes.GetAsync(application, current.Number) is not null)
                changesUrl = $"/{application}/v{current.Number}/changes";
        }

        return (sb.ToString(), changesUrl);
    }

    private static string BuildVersionDetails(DocumentVersionEntry entry, bool isLatest)
    {
        var sb = new StringBuilder("<div class=\"version-details\">");
        sb.Append($"<div>v{entry.Number}{(isLatest ? " (latest)" : "")} &middot; {entry.CreatedAt:yyyy-MM-dd}</div>");
        foreach (VersionedRepositoryCommit r in entry.Repositories)
            sb.Append($"<div class=\"repo-commit\"><span>{r.RepositoryName}</span><code>{Formatting.ShortSha(r.CommitSha)}</code></div>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private static async Task<string> BuildNavAsync(IDocumentStore store, string application, int version, string currentPath, JsonSerializerOptions jsonOptions)
    {
        IReadOnlyList<string> allPaths = await store.ListAsync(application);
        string prefix = $"v{version}/";
        string manifestPath = prefix + DocumentManifest.FileName;
        Dictionary<string, int> order = new();
        string? manifestJson = await store.ReadAsync(application, manifestPath);
        if (manifestJson is not null)
        {
            try { order = JsonSerializer.Deserialize<DocumentManifest>(manifestJson, jsonOptions)?.Pages.ToDictionary(p => p.Path, p => p.Order) ?? new(); }
            catch (JsonException) { /* fall back to unordered */ }
        }

        // Group first (product-specification, technical-specification, ...), THEN order within the group —
        // each projection numbers its own pages from 0, so sorting by Order alone (ignoring group) interleaves
        // e.g. product-specification/overview (0) with technical-specification/architecture (0).
        var pages = allPaths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p != manifestPath)
            .Select(p => p[prefix.Length..])
            .OrderBy(p => p.Contains('/') ? p.Split('/')[0] : "")
            .ThenBy(p => order.GetValueOrDefault(p, 0))
            .ToList();

        var sb = new StringBuilder("<nav>");
        string? currentGroup = null;
        foreach (string p in pages)
        {
            string group = p.Contains('/') ? p.Split('/')[0] : "";
            if (group != currentGroup)
            {
                if (currentGroup is not null) sb.Append("</ul>");
                sb.Append($"<h4>{Formatting.Humanize(group)}</h4><ul>");
                currentGroup = group;
            }
            string title = Formatting.Humanize(Path.GetFileNameWithoutExtension(p));
            string cssClass = p == currentPath ? " class=\"current\"" : "";
            sb.Append($"<li><a href=\"/{application}/v{version}/{p}\"{cssClass}>{title}</a></li>");
        }
        sb.Append("</ul></nav>");

        return sb.ToString();
    }
}
