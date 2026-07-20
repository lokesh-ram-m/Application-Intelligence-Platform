using System.Text;
using System.Text.Json;

using Aip.Abstractions.Documents;
using Aip.Abstractions.History;
using Aip.Infrastructure;
using Aip.Infrastructure.AzureBlob;

using Markdig;

using Serilog;
using Serilog.Extensions.Hosting;

// ==========================================================================
//  Application Intelligence Platform — Document Viewer
//
//  The reader half of the Creator/Viewer split. Reads documentation LIVE from IDocumentStore on every
//  request — nothing is cached or materialized to disk. The Creator (Aip.Host) writes to the store;
//  this app is the only thing that ever renders it for humans to read.
// ==========================================================================

var builder = WebApplication.CreateBuilder(args);

// Configuration: identical layering to the Creator (Aip.Host) — appsettings.json (solution root,
// committed) → appsettings.Development.json (solution root, gitignored) → environment variables
// (always wins). Both apps read the same files, so they always agree on where docs live.
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(FindSolutionRoot() ?? Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables();

// Serilog: console + the same SQL Server database Run History/Aip.Host use (a Logs table) — see
// Aip.Infrastructure/LoggingModule for the shared setup both entry points call. AddAipLogging assigns
// Log.Logger and wires it into Microsoft.Extensions.Logging (ILogger<T>) via AddSerilog — that alone
// covers Aip.Host, a plain console app with a bare ServiceCollection and no ASP.NET Core pipeline. The
// Viewer additionally needs UseSerilogRequestLogging() below, whose middleware is built by reflection
// over Serilog.Extensions.Hosting.DiagnosticContext's constructor (Serilog.ILogger, then exposed via
// IDiagnosticContext) — the exact three registrations Serilog.Extensions.Hosting's own IHostBuilder.
// UseSerilog() integration performs. Registered explicitly here (not in the shared module) since only
// the Viewer's HTTP pipeline needs them.
builder.Services.AddAipLogging(builder.Configuration, "Aip.Viewer");
builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton<DiagnosticContext>();
builder.Services.AddSingleton<IDiagnosticContext>(sp => sp.GetRequiredService<DiagnosticContext>());

// Document store selection — identical logic to Aip.Host/PlatformComposition.cs, so the Creator and
// Viewer always resolve to the same store without either one needing to know which is active.
builder.Services.AddSingleton<IDocumentStore, FileSystemDocumentStore>();
string? blobConnection = Environment.GetEnvironmentVariable("AIP_BLOB_CONNECTION_STRING") ?? builder.Configuration["Storage:ConnectionString"];
if (!string.IsNullOrWhiteSpace(blobConnection))
{
    string container = Environment.GetEnvironmentVariable("AIP_BLOB_CONTAINER") ?? builder.Configuration["Storage:Container"] ?? "documents";
    builder.Services.AddSingleton<IDocumentStore>(_ => new AzureBlobDocumentStore(blobConnection, container));
}

// "What Changed" support — reads the same SQL Server database the Creator (Aip.Host) already writes
// version-change records to. See InfrastructureModule.AddAipVersionChanges for why this is a narrower
// registration than the Creator's full AddAipInfrastructure.
builder.Services.AddAipVersionChanges(builder.Configuration);

MarkdownPipeline markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

WebApplication app = builder.Build();
app.UseSerilogRequestLogging();

// Landing page — every application that currently has documentation, read from the shared index
// (the Creator updates this after each run; see ExecutionPipeline.UpdateApplicationsIndexAsync).
app.MapGet("/", async (IDocumentStore store) =>
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
              <code>dotnet run --project src/Aip.Host -- run --config apps.yml</code>
            </div>
            """);
    }
    else
    {
        sb.Append("<div class=\"app-grid\">");
        foreach (ApplicationIndexEntry a in apps.OrderBy(a => a.Name))
            sb.Append($"""
                <a class="app-card" href="/{a.Slug}/product-specification/overview">
                  <span class="app-card-name">{a.Name}</span>
                  <span class="app-card-link">View documentation &rarr;</span>
                </a>
                """);
        sb.Append("</div>");
    }

    return Results.Content(LandingHtml(sb.ToString()), "text/html");
});

// Pinned to a specific version — the real render handler. ASP.NET Core's endpoint routing resolves this
// ahead of the unversioned catch-all below by segment specificity (literal "v" + int-constrained segment
// beats {*path}), regardless of which route is registered first.
app.MapGet("/{application}/v{version:int}/{*path}", async (string application, int version, string path, IDocumentStore store, IVersionChangeStore changes) =>
    await RenderPageAsync(store, changes, application, version, path, jsonOptions, markdownPipeline));

// "What changed" — a literal "changes" segment beats the {*path} catch-all above by the same
// specificity rule, so this always wins for that exact URL regardless of registration order.
app.MapGet("/{application}/v{version:int}/changes", async (string application, int version, IVersionChangeStore changes) =>
{
    DocumentVersionChange? change = await changes.GetAsync(application, version);

    return change is null
        ? Results.Content(NotFoundHtml("There's no change record for this version — it may be the first documented version, or nothing changed here."), "text/html", statusCode: StatusCodes.Status404NotFound)
        : Results.Content(ChangesPageHtml(application, version, change, markdownPipeline), "text/html");
});

// No version in the URL — resolve "latest" from the version index and redirect to the pinned URL. 302
// (not 301) because "latest" changes over time; a cached permanent redirect would go stale.
app.MapGet("/{application}/{*path}", async (string application, string path, IDocumentStore store) =>
{
    DocumentVersionsIndex versions = await ReadVersionsIndexAsync(store, application, jsonOptions);
    if (versions.Versions.Count == 0)
        return Results.Content(NotFoundHtml($"There's no documentation for \"{application}\" yet."), "text/html", statusCode: StatusCodes.Status404NotFound);
    int latest = versions.Versions.Max(v => v.Number);

    return Results.Redirect($"/{application}/v{latest}/{path}");
});

try { app.Run(); }
finally { await Log.CloseAndFlushAsync(); }

// ---- helpers ----

static async Task<IResult> RenderPageAsync(IDocumentStore store, IVersionChangeStore changes, string application, int version, string path,
    JsonSerializerOptions jsonOptions, MarkdownPipeline markdownPipeline)
{
    string relativePath = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path : path + ".md";
    string? markdown = await store.ReadAsync(application, $"v{version}/{relativePath}");
    if (markdown is null)
        return Results.Content(NotFoundHtml("The page you're looking for may have moved, been renamed, or belongs to a version that's no longer available."), "text/html", statusCode: StatusCodes.Status404NotFound);

    DocumentVersionsIndex versions = await ReadVersionsIndexAsync(store, application, jsonOptions);
    string navHtml = await BuildNavAsync(store, application, version, relativePath, jsonOptions);
    string versionPickerHtml = await BuildVersionPickerAsync(changes, application, version, versions, relativePath);
    string bodyHtml = Markdown.ToHtml(markdown, markdownPipeline);
    bool aiWritten = await IsAiWrittenAsync(store, application, version, relativePath, jsonOptions);

    return Results.Content(PageHtml(application, relativePath, navHtml, versionPickerHtml, bodyHtml, aiWritten), "text/html");
}

// The manifest is per-page provenance (see DocumentManifestEntry.AiWritten) — read fresh on every
// request, same as everything else here, so the badge only ever shows on the exact page it's true for
// rather than unconditionally on all of them.
static async Task<bool> IsAiWrittenAsync(IDocumentStore store, string application, int version, string relativePath, JsonSerializerOptions jsonOptions)
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

static async Task<DocumentVersionsIndex> ReadVersionsIndexAsync(IDocumentStore store, string application, JsonSerializerOptions jsonOptions)
{
    string? json = await store.ReadAsync(application, DocumentVersionsIndex.FileName);
    if (json is null) return new DocumentVersionsIndex(new List<DocumentVersionEntry>());
    try { return JsonSerializer.Deserialize<DocumentVersionsIndex>(json, jsonOptions) ?? new DocumentVersionsIndex(new List<DocumentVersionEntry>()); }
    catch (JsonException) { return new DocumentVersionsIndex(new List<DocumentVersionEntry>()); }
}

// Every page shows exactly one provenance badge — which icon/tooltip depends on how that specific page
// was produced (see DocumentManifestEntry.AiWritten), mirroring the two-way "AI-written" / "Deterministic"
// distinction the old inline page footer used to carry before it moved here.
static string ProvenanceBadgeHtml(bool aiWritten) => aiWritten
    ? "<div class=\"ai-badge\" data-tooltip=\"This page's narrative was generated with AI assistance and may contain inaccuracies.\">🧠</div>"
    : "<div class=\"ai-badge\" data-tooltip=\"Generated directly from the Knowledge Model — no AI involved.\">⚙️</div>";

static async Task<string> BuildVersionPickerAsync(IVersionChangeStore changes, string application, int currentVersion, DocumentVersionsIndex versions, string relativePath)
{
    if (versions.Versions.Count == 0) return "";
    int latest = versions.Versions.Max(v => v.Number);

    var sb = new StringBuilder("<select class=\"version-picker\" onchange=\"location.href=this.value\">");
    foreach (DocumentVersionEntry v in versions.Versions.OrderByDescending(v => v.Number))
    {
        string selected = v.Number == currentVersion ? " selected" : "";
        sb.Append($"<option value=\"/{application}/v{v.Number}/{relativePath}\"{selected}>v{v.Number}</option>");
    }
    sb.Append("</select>");

    DocumentVersionEntry? current = versions.Versions.FirstOrDefault(v => v.Number == currentVersion);
    if (current is not null)
    {
        sb.Append(BuildVersionDetails(current, current.Number == latest));

        // "What changed" only makes sense once there's a predecessor to compare against — never for v1,
        // and only when a change record actually exists (a publish that hit the empty-diff skip has none).
        if (current.Number > 1 && await changes.GetAsync(application, current.Number) is not null)
            sb.Append($"<a class=\"changes-link\" href=\"/{application}/v{current.Number}/changes\">What changed &rarr;</a>");
    }

    return sb.ToString();
}

static string BuildVersionDetails(DocumentVersionEntry entry, bool isLatest)
{
    var sb = new StringBuilder("<div class=\"version-details\">");
    sb.Append($"<div>v{entry.Number}{(isLatest ? " (latest)" : "")} &middot; {entry.CreatedAt:yyyy-MM-dd}</div>");
    foreach (VersionedRepositoryCommit r in entry.Repositories)
        sb.Append($"<div class=\"repo-commit\"><span>{r.RepositoryName}</span><code>{ShortSha(r.CommitSha)}</code></div>");
    sb.Append("</div>");

    return sb.ToString();
}

static string ShortSha(string sha) => sha[..Math.Min(7, sha.Length)];

static async Task<string> BuildNavAsync(IDocumentStore store, string application, int version, string currentPath, JsonSerializerOptions jsonOptions)
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
            sb.Append($"<h4>{Humanize(group)}</h4><ul>");
            currentGroup = group;
        }
        string title = Humanize(Path.GetFileNameWithoutExtension(p));
        string cssClass = p == currentPath ? " class=\"current\"" : "";
        sb.Append($"<li><a href=\"/{application}/v{version}/{p}\"{cssClass}>{title}</a></li>");
    }
    sb.Append("</ul></nav>");

    return sb.ToString();
}

static string Humanize(string value) =>
    string.IsNullOrEmpty(value) ? value :
    string.Join(' ', value.Split('-', '_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

static string? FindSolutionRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (dir.GetFiles("Aip.slnx").Length > 0) return dir.FullName;
        dir = dir.Parent;
    }

    return null;
}

// Application logo, embedded as a data URI so the viewer stays a single self-contained binary (no wwwroot/static file serving).
const string LogoDataUri = "data:image/png;base64,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";

// Synergech theme: navy primary, warm gold accent, white/near-white surfaces. One shared palette so the
// landing page and doc pages never drift apart visually.
static string Theme() => """
    :root {
      --navy: #0f2f5f;
      --navy-dark: #0a2247;
      --blue: #1f5ea8;
      --blue-light: #eaf2fb;
      --gold: #f5b301;
      --gold-dark: #d99b00;
      --white: #ffffff;
      --gray-50: #f7f9fc;
      --gray-100: #eef1f6;
      --gray-200: #dfe4ec;
      --gray-500: #64748b;
      --gray-900: #1a2333;
    }
    """;

static string Styles() => """
    * { box-sizing: border-box; }
    body {
      font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
      line-height: 1.65;
      margin: 0;
      color: var(--gray-900);
      background: var(--gray-50);
    }
    a { color: var(--blue); text-decoration: none; }
    a:hover { text-decoration: underline; }
    h1, h2, h3, h4 { color: var(--navy); font-weight: 700; }
    h1 { font-size: 1.9rem; border-bottom: 3px solid var(--gold); padding-bottom: 0.5rem; margin-top: 0; }
    h2 { font-size: 1.35rem; margin-top: 2.2rem; }
    h3 { font-size: 1.1rem; margin-top: 1.6rem; }
    table { border-collapse: collapse; width: 100%; margin: 1rem 0; background: var(--white); }
    th, td { border: 1px solid var(--gray-200); padding: 8px 12px; text-align: left; }
    th { background: var(--navy); color: var(--white); font-weight: 600; }
    tr:nth-child(even) td { background: var(--gray-50); }
    code { background: var(--gray-100); padding: 2px 5px; border-radius: 4px; font-size: 0.9em; }
    pre code { display: block; padding: 14px; overflow-x: auto; border-radius: 8px; background: var(--gray-900); color: var(--gray-50); }
    .topbar {
      background: var(--navy);
      background-image: linear-gradient(90deg, var(--navy) 0%, var(--navy-dark) 100%);
      color: var(--white);
      padding: 0.9rem 2rem;
      display: flex;
      align-items: center;
      gap: 0.6rem;
      border-bottom: 3px solid var(--gold);
    }
    .topbar a { color: var(--white); font-weight: 700; font-size: 1.05rem; letter-spacing: 0.02em; display: flex; align-items: center; gap: 0.6rem; }
    .topbar a:hover { text-decoration: none; opacity: 0.85; }
    .topbar img.logo { width: 28px; height: 28px; border-radius: 4px; }
    """;

static string LandingHtml(string bodyHtml) => $$"""
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8" />
      <title>Application Intelligence Platform</title>
      <style>
        {{Theme()}}
        {{Styles()}}
        .hero { max-width: 960px; margin: 0 auto; padding: 3rem 1.5rem 1rem; }
        .hero h1 { border: none; font-size: 2.1rem; }
        .hero p { color: var(--gray-500); margin-top: -0.5rem; }
        .app-grid { max-width: 960px; margin: 1.5rem auto 3rem; padding: 0 1.5rem; display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 1rem; }
        .app-card {
          display: flex; flex-direction: column; gap: 0.5rem;
          background: var(--white); border: 1px solid var(--gray-200); border-left: 4px solid var(--gold);
          border-radius: 10px; padding: 1.2rem 1.4rem; transition: box-shadow 0.15s, transform 0.15s;
        }
        .app-card:hover { box-shadow: 0 6px 20px rgba(15, 47, 95, 0.12); transform: translateY(-2px); text-decoration: none; }
        .app-card-name { font-size: 1.1rem; font-weight: 700; color: var(--navy); }
        .app-card-link { font-size: 0.85rem; color: var(--blue); }
        .empty-state { max-width: 960px; margin: 3rem auto; padding: 0 1.5rem; color: var(--gray-500); }
        .empty-state code { display: inline-block; margin-top: 0.5rem; }
      </style>
    </head>
    <body>
      <div class="topbar"><a href="/"><img class="logo" src="{{LogoDataUri}}" alt="" />Application Intelligence Platform</a></div>
      <div class="hero">
        <h1>Documented Applications</h1>
        <p>Live documentation, generated from each application's Knowledge Model.</p>
      </div>
      {{bodyHtml}}
    </body>
    </html>
    """;

// Themed 404 — both "no documentation for this application at all" and "this page/version doesn't exist"
// route through here, so a stale bookmark or an old version number reads as a friendly not-found page
// instead of a raw text body (Results.NotFound's default rendering) or exposed internal paths/store names.
// The illustration is inline SVG (no external image request) — a page-with-a-question-mark, the same
// motif most sites use for "we couldn't find that," in the app's own navy/gold palette instead of a stock
// generic one.
static string NotFoundHtml(string tagline) => $$"""
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8" />
      <title>Page not found — Application Intelligence Platform</title>
      <style>
        {{Theme()}}
        {{Styles()}}
        .not-found { max-width: 480px; margin: 4rem auto; padding: 0 1.5rem; text-align: center; }
        .not-found svg { width: 160px; height: 160px; margin-bottom: 0.5rem; }
        .not-found h1 { border: none; font-size: 1.6rem; margin: 0.4rem 0 0.6rem; }
        .not-found p { color: var(--gray-500); margin-bottom: 1.8rem; }
        .not-found .home-link {
          display: inline-block; background: var(--navy); color: var(--white); font-weight: 600;
          padding: 10px 22px; border-radius: 8px; text-decoration: none;
        }
        .not-found .home-link:hover { background: var(--navy-dark); text-decoration: none; }
      </style>
    </head>
    <body>
      <div class="topbar"><a href="/"><img class="logo" src="{{LogoDataUri}}" alt="" />Application Intelligence Platform</a></div>
      <div class="not-found">
        <svg viewBox="0 0 120 120" fill="none" xmlns="http://www.w3.org/2000/svg">
          <rect x="28" y="14" width="56" height="76" rx="6" fill="var(--blue-light)" stroke="var(--navy)" stroke-width="3" />
          <line x1="38" y1="32" x2="66" y2="32" stroke="var(--navy)" stroke-width="3" stroke-linecap="round" />
          <line x1="38" y1="42" x2="74" y2="42" stroke="var(--navy)" stroke-width="3" stroke-linecap="round" />
          <line x1="38" y1="52" x2="60" y2="52" stroke="var(--navy)" stroke-width="3" stroke-linecap="round" />
          <circle cx="80" cy="76" r="20" fill="var(--white)" stroke="var(--gold-dark)" stroke-width="4" />
          <text x="80" y="84" text-anchor="middle" font-size="22" font-weight="700" fill="var(--gold-dark)" font-family="Segoe UI, sans-serif">?</text>
        </svg>
        <h1>We couldn't find that page</h1>
        <p>{{tagline}}</p>
        <a class="home-link" href="/">Go to all applications</a>
      </div>
    </body>
    </html>
    """;

static string PageHtml(string application, string path, string navHtml, string versionPickerHtml, string bodyHtml, bool aiWritten) => $$"""
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8" />
      <title>{{application}} — {{path}}</title>
      <script src="https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js"></script>
      <style>
        {{Theme()}}
        {{Styles()}}
        .layout { display: flex; width: 100%; margin: 0; align-items: flex-start; }
        .nav-rail {
          width: 240px; flex-shrink: 0; padding: 1.5rem 1.2rem;
          background: var(--white); border-right: 1px solid var(--gray-200);
          position: sticky; top: 0; height: 100vh; overflow-y: auto;
        }
        .nav-rail > a:first-child { display: inline-block; font-size: 0.85rem; color: var(--gray-500); margin-bottom: 1rem; }
        .nav-rail > a:first-child:hover { color: var(--navy); }
        .nav-rail h4 {
          margin: 1.3rem 0 0.5rem; font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.06em;
          color: var(--gray-500); font-weight: 700;
        }
        .nav-rail ul { list-style: none; padding: 0; margin: 0; }
        .nav-rail li { margin: 1px 0; }
        .nav-rail a {
          display: block; text-decoration: none; font-size: 0.9em; color: var(--gray-900);
          padding: 5px 10px; border-radius: 6px; border-left: 3px solid transparent;
        }
        .nav-rail a:hover { background: var(--blue-light); text-decoration: none; }
        .nav-rail a.current { font-weight: 700; color: var(--navy); background: var(--blue-light); border-left-color: var(--gold); }
        main { flex: 1; padding: 2.5rem 3rem; background: var(--white); min-height: 100vh; }
        .side-rail {
          width: 180px; flex-shrink: 0; padding: 1.5rem 1.2rem;
          display: flex; flex-direction: column; align-items: flex-end; gap: 0.9rem;
          position: sticky; top: 0;
        }
        .version-picker {
          width: 100%; padding: 5px 8px; font-size: 0.82rem;
          border: 1px solid var(--gray-200); border-radius: 6px; background: var(--gray-50); color: var(--gray-900);
        }
        .version-details {
          width: 100%; display: flex; flex-direction: column; gap: 8px;
          font-size: 0.72rem; color: var(--gray-500); text-align: right;
        }
        .version-details code { background: none; padding: 0; font-size: 0.72rem; color: var(--gray-500); }
        .repo-commit { display: flex; flex-direction: column; gap: 1px; }
        .repo-commit span { font-weight: 600; color: var(--gray-900); }
        .changes-link { font-size: 0.78rem; font-weight: 600; color: var(--blue); }
        .ai-badge { position: relative; cursor: default; font-size: 1.4rem; line-height: 1; opacity: 0.75; transition: opacity 0.15s; }
        .ai-badge:hover { opacity: 1; }
        .ai-badge::after {
          content: attr(data-tooltip);
          position: absolute; right: 0; top: calc(100% + 8px);
          background: var(--navy); color: var(--white); font-size: 0.78rem; line-height: 1.45;
          padding: 8px 10px; border-radius: 6px; width: 190px; text-align: left;
          opacity: 0; visibility: hidden; transform: translateY(-4px);
          transition: opacity 0.15s, transform 0.15s; pointer-events: none; z-index: 10;
          box-shadow: 0 6px 20px rgba(15, 47, 95, 0.25);
        }
        .ai-badge:hover::after { opacity: 1; visibility: visible; transform: translateY(0); }
      </style>
    </head>
    <body>
      <div class="topbar"><a href="/"><img class="logo" src="{{LogoDataUri}}" alt="" />Application Intelligence Platform</a></div>
      <div class="layout">
        <aside class="nav-rail"><a href="/">&larr; All applications</a>{{navHtml}}</aside>
        <main>
          {{bodyHtml}}
        </main>
        <aside class="side-rail">
          {{ProvenanceBadgeHtml(aiWritten)}}
          {{versionPickerHtml}}
        </aside>
      </div>
      <script>
        // Markdig renders ```mermaid fenced blocks as <code class="language-mermaid">; mermaid.js expects
        // <pre class="mermaid">, so convert before initializing.
        document.querySelectorAll('code.language-mermaid').forEach(function (el) {
          var pre = document.createElement('pre');
          pre.className = 'mermaid';
          pre.textContent = el.textContent;
          el.closest('pre').replaceWith(pre);
        });
        mermaid.initialize({ startOnLoad: true, theme: 'neutral' });
      </script>
    </body>
    </html>
    """;

// The AI-authored (or deterministic-fallback) changelog is rendered through the same Markdig pipeline as
// every other page — the prompt asks for plain prose/short bullets but doesn't forbid markdown syntax, and
// Markdig safely handles either shape (real markdown renders, anything else passes through as plain text).
static string ChangesPageHtml(string application, int version, DocumentVersionChange change, MarkdownPipeline markdownPipeline) => $$"""
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8" />
      <title>{{application}} — What changed in v{{version}}</title>
      <style>
        {{Theme()}}
        {{Styles()}}
        .changes { max-width: 720px; margin: 0 auto; padding: 2.5rem 1.5rem 4rem; }
        .changes .back-link { display: inline-block; font-size: 0.85rem; color: var(--gray-500); margin-bottom: 1rem; }
        .changes .back-link:hover { color: var(--navy); }
        .summary-card {
          background: var(--white); border: 1px solid var(--gray-200); border-left: 4px solid var(--gold);
          border-radius: 10px; padding: 1.4rem 1.6rem; margin: 1rem 0 1.6rem;
        }
        .summary-card p:first-child { margin-top: 0; }
        .summary-card p:last-child { margin-bottom: 0; }
        .stat-strip { display: flex; flex-wrap: wrap; gap: 0.8rem; margin-bottom: 1.6rem; }
        .stat {
          background: var(--white); border: 1px solid var(--gray-200); border-radius: 8px;
          padding: 0.7rem 1.1rem; font-size: 0.82rem; color: var(--gray-500); min-width: 110px;
        }
        .stat strong { display: block; font-size: 1.4rem; color: var(--navy); }
        .repo-deltas h3 { margin-bottom: 0.6rem; }
        .repo-deltas .repo-commit {
          display: flex; justify-content: space-between; align-items: center;
          padding: 8px 0; border-bottom: 1px solid var(--gray-100); font-size: 0.85rem;
        }
        .repo-deltas .repo-commit span { font-weight: 600; }
        .repo-deltas .repo-commit code { font-size: 0.78rem; }
      </style>
    </head>
    <body>
      <div class="topbar"><a href="/"><img class="logo" src="{{LogoDataUri}}" alt="" />Application Intelligence Platform</a></div>
      <div class="changes">
        <a class="back-link" href="/{{application}}/v{{version}}/product-specification/overview">&larr; Back to {{application}} v{{version}}</a>
        <h1>What changed — v{{change.PreviousVersionNumber}} &rarr; v{{version}}</h1>
        <div class="summary-card">{{Markdown.ToHtml(change.Summary, markdownPipeline)}}</div>
        <div class="stat-strip">
          <div class="stat"><strong>{{change.NodesAdded}}</strong>nodes added</div>
          <div class="stat"><strong>{{change.NodesRemoved}}</strong>nodes removed</div>
          <div class="stat"><strong>{{change.RelationshipsAdded}}</strong>relationships added</div>
          <div class="stat"><strong>{{change.RelationshipsRemoved}}</strong>relationships removed</div>
        </div>
        <div class="repo-deltas">
          <h3>Repository commits</h3>
          {{BuildRepoDeltasHtml(change.RepositoryCommits)}}
        </div>
      </div>
    </body>
    </html>
    """;

static string BuildRepoDeltasHtml(IReadOnlyList<RepositoryCommitChange> commits)
{
    var sb = new StringBuilder();
    foreach (RepositoryCommitChange r in commits)
    {
        string from = r.PreviousCommit is null ? "new" : ShortSha(r.PreviousCommit);
        sb.Append($"<div class=\"repo-commit\"><span>{r.RepositoryName}</span><code>{from} &rarr; {ShortSha(r.NewCommit)}</code></div>");
    }

    return sb.ToString();
}
