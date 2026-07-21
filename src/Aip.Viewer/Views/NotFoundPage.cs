namespace Aip.Viewer.Views;

// Themed 404 — both "no documentation for this application at all" and "this page/version doesn't exist"
// route through here, so a stale bookmark or an old version number reads as a friendly not-found page
// instead of a raw text body (Results.NotFound's default rendering) or exposed internal paths/store names.
// The illustration is inline SVG (no external image request) — a page-with-a-question-mark, the same
// motif most sites use for "we couldn't find that," in the app's own navy/gold palette instead of a stock
// generic one.
internal static class NotFoundPage
{
    internal static string Render(string tagline) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8" />
          <title>Page not found — DocSynth</title>
          <style>
            {{Layout.Theme()}}
            {{Layout.Styles()}}
            {{Styles()}}
          </style>
        </head>
        <body>
          {{Layout.Topbar()}}
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

    private static string Styles() => """
        .not-found { max-width: 480px; margin: 4rem auto; padding: 0 1.5rem; text-align: center; }
        .not-found svg { width: 160px; height: 160px; margin-bottom: 0.5rem; }
        .not-found h1 { border: none; font-size: 1.6rem; margin: 0.4rem 0 0.6rem; }
        .not-found p { color: var(--gray-500); margin-bottom: 1.8rem; }
        .not-found .home-link {
          display: inline-block; background: var(--navy); color: var(--white); font-weight: 600;
          padding: 10px 22px; border-radius: 8px; text-decoration: none;
        }
        .not-found .home-link:hover { background: var(--navy-dark); text-decoration: none; }
        """;
}
