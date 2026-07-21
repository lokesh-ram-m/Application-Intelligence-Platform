using System.Text;

using Aip.Abstractions.History;
using Aip.Viewer;

using Markdig;

namespace Aip.Viewer.Views;

// The standalone "What changed in v{N}" page — also the source the doc pages' slide-in panel fetches
// and lifts #changes-content out of (see DocumentPage.Script's openChangesPanel), so the content only
// ever gets defined in one place.
internal static class ChangesPage
{
    // The AI-authored (or deterministic-fallback) changelog is rendered through the same Markdig pipeline as
    // every other page — the prompt asks for plain prose/short bullets but doesn't forbid markdown syntax, and
    // Markdig safely handles either shape (real markdown renders, anything else passes through as plain text).
    internal static string Render(string application, int version, DocumentVersionChange change, MarkdownPipeline markdownPipeline) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8" />
          <title>{{application}} — What changed in v{{version}}</title>
          <style>
            {{Layout.Theme()}}
            {{Layout.Styles()}}
            {{Styles()}}
          </style>
        </head>
        <body>
          {{Layout.Topbar()}}
          <div class="changes">
            <a class="back-link" href="/{{application}}/v{{version}}/product-specification/overview">&larr; Back to {{application}} v{{version}}</a>
            <!-- #changes-content is the reusable fragment the doc pages' slide-in panel fetches and lifts out
                 (see DocumentPage.Script's openChangesPanel) — the back-link above stays outside it since it
                 only makes sense on this standalone page, not inside a panel that's already open on top of it. -->
            <div id="changes-content">
              <div class="changes-header">
                <h2>What changed</h2>
                <div class="changes-subtext">v{{change.PreviousVersionNumber}} &rarr; v{{version}}</div>
              </div>
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
              {{BuildCompositeImpactHtml(change)}}
            </div>
          </div>
        </body>
        </html>
        """;

    private static string BuildRepoDeltasHtml(IReadOnlyList<RepositoryCommitChange> commits)
    {
        var sb = new StringBuilder();
        foreach (RepositoryCommitChange r in commits)
        {
            string from = r.PreviousCommit is null ? "new" : Formatting.ShortSha(r.PreviousCommit);
            sb.Append($"<div class=\"repo-commit\"><span>{r.RepositoryName}</span><code>{from} &rarr; {Formatting.ShortSha(r.NewCommit)}</code></div>");
        }

        return sb.ToString();
    }

    // PerApplicationImpact is only ever populated when this version's diff pulled in at least one CHILD's
    // changes (see Aip.Core.Domain.DiffGrouping.IsCompositeImpact) — empty for an ordinary leaf application,
    // non-empty even when only ONE child changed this run (that's still worth breaking out by name).
    private static string BuildCompositeImpactHtml(DocumentVersionChange change)
    {
        if (change.PerApplicationImpact.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("""<div class="composite-impact"><h3>Impact by sub-application</h3>""");
        foreach (OwningApplicationImpact impact in change.PerApplicationImpact)
        {
            sb.Append($"""
                <div class="app-impact-row">
                  <span class="app-impact-name">{impact.Application}</span>
                  <span class="app-impact-counts">+{impact.NodesAdded}/-{impact.NodesRemoved} nodes &middot; +{impact.RelationshipsAdded}/-{impact.RelationshipsRemoved} relationships</span>
                </div>
                """);
        }
        sb.Append("</div>");

        if (change.AddedIntegrationNames.Count > 0 || change.RemovedIntegrationNames.Count > 0)
        {
            sb.Append("""<div class="composite-impact"><h3>Integrations between sub-applications</h3>""");
            foreach (string name in change.AddedIntegrationNames) sb.Append($"""<div class="integration-row added">+ {name}</div>""");
            foreach (string name in change.RemovedIntegrationNames) sb.Append($"""<div class="integration-row removed">- {name}</div>""");
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static string Styles() => """
        .changes { max-width: 720px; margin: 0 auto; padding: 2.5rem 1.5rem 4rem; }
        .changes .back-link { display: inline-block; font-size: 0.85rem; color: var(--gray-500); margin-bottom: 1rem; }
        .changes .back-link:hover { color: var(--navy); }
        .composite-impact { margin-top: 1.6rem; }
        .composite-impact h3 { margin-bottom: 0.6rem; }
        .app-impact-row {
          display: flex; justify-content: space-between; align-items: center; gap: 12px;
          padding: 10px 0; border-bottom: 1px solid var(--gray-100); font-size: 0.85rem;
        }
        .app-impact-name { font-weight: 600; }
        .app-impact-counts { color: var(--gray-500); font-size: 0.78rem; }
        .integration-row { font-size: 0.85rem; padding: 4px 0; }
        .integration-row.added { color: #1a7a3c; }
        .integration-row.removed { color: #b3261e; }
        """;
}
