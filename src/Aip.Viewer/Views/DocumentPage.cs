namespace Aip.Viewer.Views;

// The main documentation page: nav rail, rendered markdown, version picker, and the "What changed"
// slide-in panel. Markup, CSS, and JS are kept as separate methods below so each can be read (and
// changed) independently of the others.
internal static class DocumentPage
{
    internal static string Render(string application, string path, string navHtml, string versionPickerHtml, string bodyHtml, bool aiWritten, string? changesUrl, IReadOnlyList<string>? children = null)
    {
        // Only rendered when a change record exists for this version (see DocumentEndpoints.BuildVersionPickerAsync) —
        // the button and its slide-in panel are inert markup otherwise, not just hidden via CSS, so a page
        // with nothing to show never ships the extra DOM/JS wiring at all. The button lives in the side
        // rail (next to the version picker it relates to) while the panel/backdrop are viewport-level
        // overlays, so they're built as two separate fragments even though both are gated on the same
        // null-check.
        string changesButtonHtml = ChangesButtonHtml(changesUrl);
        string changesOverlayHtml = ChangesOverlayHtml(changesUrl);
        // Structural, not AI-dependent — a composite application's side rail always lists its children
        // regardless of what the AI-written overview prose says, exactly like the provenance badge/version
        // picker beside it.
        string subApplicationsHtml = SubApplicationsHtml(children);

        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8" />
          <title>{{application}} — {{path}}</title>
          <script src="https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js"></script>
          <style>
            {{Layout.Theme()}}
            {{Layout.Styles()}}
            {{Styles()}}
          </style>
        </head>
        <body>
          {{Layout.Topbar()}}
          <div class="layout">
            <aside class="nav-rail"><a href="/">&larr; All applications</a>{{navHtml}}</aside>
            <main>
              {{bodyHtml}}
            </main>
            <aside class="side-rail">
              {{ProvenanceBadgeHtml(aiWritten)}}
              {{versionPickerHtml}}
              {{changesButtonHtml}}
              {{subApplicationsHtml}}
            </aside>
          </div>
          {{changesOverlayHtml}}
          <script>
            {{Script()}}
          </script>
        </body>
        </html>
        """;
    }

    // Every page shows exactly one provenance badge — which icon/tooltip depends on how that specific page
    // was produced (see DocumentManifestEntry.AiWritten), mirroring the two-way "AI-written" / "Deterministic"
    // distinction the old inline page footer used to carry before it moved here.
    private static string ProvenanceBadgeHtml(bool aiWritten) => aiWritten
        ? "<div class=\"ai-badge\" data-tooltip=\"This page's narrative was generated with AI assistance and may contain inaccuracies.\">🧠</div>"
        : "<div class=\"ai-badge\" data-tooltip=\"Generated directly from the Knowledge Model — no AI involved.\">⚙️</div>";

    private static string SubApplicationsHtml(IReadOnlyList<string>? children)
    {
        if (children is null || children.Count == 0) return "";
        // Link by slug, not raw name, matching how the landing page links to every application — for a
        // typical alphanumeric app name the two are identical, but this stays correct if they ever diverge.
        var links = string.Join("", children.Select(c => $"<li><a href=\"/{Aip.Abstractions.Documents.DocumentPaths.SlugifyApplication(c)}/product-specification/overview\">{c}</a></li>"));

        return $"""<div class="sub-apps"><h5>Sub-applications</h5><ul>{links}</ul></div>""";
    }

    private static string ChangesButtonHtml(string? changesUrl) => changesUrl is null ? "" :
        $"""<button class="changes-btn" onclick="openChangesPanel('{changesUrl}')">What changed</button>""";

    private static string ChangesOverlayHtml(string? changesUrl) => changesUrl is null ? "" : """
        <div class="changes-backdrop" id="changes-backdrop" onclick="closeChangesPanel()"></div>
        <aside class="changes-panel" id="changes-panel">
          <button class="panel-close" onclick="closeChangesPanel()" aria-label="Close">&times;</button>
          <div id="changes-panel-body"><p class="panel-loading">Loading&hellip;</p></div>
        </aside>
        """;

    private static string Styles() => """
        .layout { display: flex; width: 100%; margin: 0; align-items: flex-start; }
        .nav-rail {
          width: 240px; flex-shrink: 0; padding: 1.5rem 1.2rem;
          background: var(--white); border-right: 1px solid var(--gray-200);
          /* Offset by the sticky topbar's own height so this sticks just below it, not underneath it. */
          position: sticky; top: var(--topbar-height); height: calc(100vh - var(--topbar-height)); overflow-y: auto;
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
          position: sticky; top: var(--topbar-height);
        }
        .version-picker {
          width: 100%; padding: 9px 30px 9px 12px; font-size: 0.85rem; font-weight: 600;
          border: 1px solid var(--gray-200); border-radius: 6px; background: var(--white); color: var(--navy);
          cursor: pointer; appearance: none; -webkit-appearance: none;
          /* Custom chevron since appearance:none drops the native one — plain gray-500 arrow, matches theme. */
          background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='14' height='14' viewBox='0 0 24 24' fill='none' stroke='%2364748b' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpolyline points='6 9 12 15 18 9'%3E%3C/polyline%3E%3C/svg%3E");
          background-repeat: no-repeat; background-position: right 10px center; background-size: 14px;
          transition: border-color 0.15s, box-shadow 0.15s;
        }
        .version-picker:hover { border-color: var(--blue); }
        .version-picker:focus { outline: none; border-color: var(--blue); box-shadow: 0 0 0 3px var(--blue-light); }
        .version-details {
          width: 100%; display: flex; flex-direction: column; gap: 8px;
          font-size: 0.72rem; color: var(--gray-500); text-align: right;
        }
        .version-details code { background: none; padding: 0; font-size: 0.72rem; color: var(--gray-500); }
        .repo-commit { display: flex; flex-direction: column; gap: 1px; }
        .repo-commit span { font-weight: 600; color: var(--gray-900); }
        .changes-btn {
          width: 100%; background: var(--blue); color: var(--white); border: none; border-radius: 6px;
          padding: 8px 12px; font-size: 0.82rem; font-weight: 600; font-family: inherit;
          cursor: pointer; transition: background 0.15s;
        }
        .changes-btn:hover { background: var(--navy); }
        .changes-backdrop {
          position: fixed; inset: 0; background: rgba(15, 47, 95, 0.35); z-index: 40;
          opacity: 0; pointer-events: none; transition: opacity 0.2s;
        }
        .changes-backdrop.open { opacity: 1; pointer-events: auto; }
        .changes-panel {
          /* Full-height, sits above the topbar (higher z-index) — deliberately overlaps it rather than
             trying to track the topbar's height, which kept drifting out of sync. */
          position: fixed; top: 0; right: 0; bottom: 0; width: min(600px, 100vw); z-index: 60;
          background: var(--white); box-shadow: -8px 0 30px rgba(15, 47, 95, 0.2);
          transform: translateX(100%); transition: transform 0.25s ease; overflow-y: auto;
          padding: 2rem 1.8rem 3rem;
        }
        .changes-panel.open { transform: translateX(0); }
        .changes-panel .panel-close {
          position: absolute; top: 1.2rem; right: 1.4rem; background: none; border: none;
          font-size: 1.3rem; line-height: 1; color: var(--gray-500); cursor: pointer; padding: 4px;
        }
        .changes-panel .panel-close:hover { color: var(--navy); }
        .changes-panel .panel-loading { color: var(--gray-500); font-size: 0.9rem; }
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
        .sub-apps { width: 100%; text-align: right; }
        .sub-apps h5 { margin: 0 0 6px; font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.06em; color: var(--gray-500); font-weight: 700; }
        .sub-apps ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
        .sub-apps a { font-size: 0.82rem; }
        """;

    private static string Script() => """
        // Markdig renders ```mermaid fenced blocks as <code class="language-mermaid">; mermaid.js expects
        // <pre class="mermaid">, so convert before initializing.
        document.querySelectorAll('code.language-mermaid').forEach(function (el) {
          var pre = document.createElement('pre');
          pre.className = 'mermaid';
          pre.textContent = el.textContent;
          el.closest('pre').replaceWith(pre);
        });
        mermaid.initialize({ startOnLoad: true, theme: 'neutral' });

        // "What changed" opens as a slide-in panel rather than a full page navigation — fetches the same
        // standalone /changes page (kept as a real route for direct links/bookmarks) and lifts out just its
        // #changes-content fragment, so the content is never duplicated between the two render paths.
        var changesLoaded = false;
        function openChangesPanel(url) {
          document.getElementById('changes-backdrop').classList.add('open');
          document.getElementById('changes-panel').classList.add('open');
          if (changesLoaded) return;
          fetch(url).then(function (r) { return r.text(); }).then(function (html) {
            var doc = new DOMParser().parseFromString(html, 'text/html');
            var content = doc.getElementById('changes-content');
            document.getElementById('changes-panel-body').innerHTML = content ? content.innerHTML : '<p>Could not load changes.</p>';
            changesLoaded = true;
          }).catch(function () {
            document.getElementById('changes-panel-body').innerHTML = '<p>Could not load changes.</p>';
          });
        }
        function closeChangesPanel() {
          document.getElementById('changes-backdrop').classList.remove('open');
          document.getElementById('changes-panel').classList.remove('open');
        }
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeChangesPanel(); });
        """;
}
