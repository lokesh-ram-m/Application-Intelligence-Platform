namespace Aip.Viewer.Views;

// The shell every page shares: logo, topbar, theme tokens, and base CSS. Kept separate from any one
// page so the landing page, doc pages, 404, and changes page can never drift out of visual sync.
internal static class Layout
{
    // Application logo, embedded as a data URI so the viewer stays a single self-contained binary (no wwwroot/static file serving).
    private const string LogoDataUri = "data:image/png;base64,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";

    // The one topbar markup shared by every page (was duplicated four times, one per render function).
    internal static string Topbar() => $"""
        <div class="topbar">
          <a href="/"><span class="logo-badge"><img class="logo" src="{LogoDataUri}" alt="" /></span>DocSynth</a>
        </div>
        """;

    // Brand theme: navy primary, warm gold accent, white/near-white surfaces. One shared palette so the
    // landing page and doc pages never drift apart visually.
    internal static string Theme() => """
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
          /* The topbar's real rendered height (0.7rem padding + the 38px logo badge + 3px border) — one
             source of truth so the nav-rail and side-rail offsets can never drift out of sync with it
             again, the way the old hardcoded "60px" guess did once the badge made it taller. */
          --topbar-height: 64px;
        }
        """;

    internal static string Styles() => """
        * { box-sizing: border-box; }
        html, body {
          overflow-x: hidden; /* never allow horizontal scroll — the topbar and layout are always full-width */
        }
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
          padding: 0.7rem 2rem;
          display: flex;
          align-items: center;
          gap: 0.85rem;
          border-bottom: 3px solid var(--gold);
          box-shadow: 0 2px 10px rgba(10, 34, 71, 0.35);
          position: sticky;
          top: 0;
          z-index: 50;
        }
        .topbar a { color: var(--white); font-weight: 700; font-size: 1.08rem; letter-spacing: 0.02em; display: flex; align-items: center; gap: 0.85rem; }
        .topbar a:hover { text-decoration: none; opacity: 0.9; }
        /* A white badge behind the mark — the logo's own blue is close enough to --navy that it disappeared
           into the header background without one; the badge gives it real contrast and a bit of polish. */
        .topbar .logo-badge {
          width: 38px; height: 38px; flex-shrink: 0; border-radius: 9px; background: var(--white);
          display: flex; align-items: center; justify-content: center; box-shadow: 0 1px 4px rgba(0, 0, 0, 0.3);
        }
        .topbar img.logo { width: 26px; height: 26px; display: block; }
        /* Shared by the standalone /changes page and the doc pages' slide-in panel — see DocumentPage and
           ChangesPage's #changes-content, which is fetched and reused as-is by both render paths. */
        .changes-header { margin-bottom: 1.2rem; }
        .changes-header h2 { margin: 0 0 0.3rem; font-size: 1.4rem; }
        .changes-subtext { font-size: 0.85rem; font-weight: 600; color: var(--gray-500); }
        .summary-card {
          background: var(--white); border: 1px solid var(--gray-200); border-left: 4px solid var(--gold);
          border-radius: 10px; padding: 1.4rem 1.6rem; margin: 1rem 0 1.6rem;
        }
        .summary-card p:first-child { margin-top: 0; }
        .summary-card p:last-child { margin-bottom: 0; }
        /* Grid, not flex-wrap — flex let each tile size to its own text ("relationships added" is longer
           than "nodes added"), so the two rows ended up visibly different widths. A fixed 2-column grid
           forces every tile to the same size regardless of label length. */
        .stat-strip { display: grid; grid-template-columns: 1fr 1fr; gap: 0.8rem; margin-bottom: 1.6rem; }
        .stat {
          background: var(--gray-50); border: 1px solid var(--gray-200); border-radius: 10px;
          padding: 0.85rem 1.1rem; font-size: 0.78rem; color: var(--gray-500);
          box-shadow: 0 1px 3px rgba(15, 47, 95, 0.08);
          transition: box-shadow 0.15s, transform 0.15s;
        }
        .stat:hover { box-shadow: 0 4px 12px rgba(15, 47, 95, 0.14); transform: translateY(-1px); }
        .stat strong { display: block; font-size: 1.5rem; color: var(--navy); margin-bottom: 2px; }
        .repo-deltas h3 { margin-bottom: 0.6rem; }
        .repo-deltas .repo-commit {
          display: flex; flex-direction: column; align-items: flex-start; gap: 2px;
          padding: 10px 0; border-bottom: 1px solid var(--gray-100); font-size: 0.85rem;
        }
        .repo-deltas .repo-commit span { font-weight: 600; }
        .repo-deltas .repo-commit code { font-size: 0.78rem; }
        """;
}
