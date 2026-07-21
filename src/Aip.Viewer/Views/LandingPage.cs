namespace Aip.Viewer.Views;

// The "/" route — every application that currently has documentation, or an empty-state hint.
internal static class LandingPage
{
    internal static string Render(string bodyHtml) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8" />
          <title>DocSynth</title>
          <style>
            {{Layout.Theme()}}
            {{Layout.Styles()}}
            {{Styles()}}
          </style>
        </head>
        <body>
          {{Layout.Topbar()}}
          <div class="hero">
            <h1>Documented Applications</h1>
            <p>Live documentation, generated from each application's Knowledge Model.</p>
          </div>
          {{bodyHtml}}
        </body>
        </html>
        """;

    private static string Styles() => """
        .hero { max-width: 960px; margin: 0 auto; padding: 3rem 1.5rem 1rem; }
        .hero h1 { border: none; font-size: 2.1rem; }
        .hero p { color: var(--gray-500); margin-top: -0.5rem; }
        .app-grid { max-width: 960px; margin: 1.5rem auto 3rem; padding: 0 1.5rem; display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 1rem; }
        .app-card {
          display: flex; flex-direction: column; gap: 0.5rem;
          background: var(--white); border: 1px solid var(--gray-200); border-left: 4px solid var(--gold);
          border-radius: 10px; padding: 1.2rem 1.4rem; transition: box-shadow 0.15s, transform 0.15s;
        }
        .app-card:hover { box-shadow: 0 6px 20px rgba(15, 47, 95, 0.12); transform: translateY(-2px); }
        /* The card itself is a plain <div> (not an <a>) since a composite card also links to each of its
           children below — an <a> cannot legally nest another <a> inside it. */
        .app-card-link-area { display: flex; flex-direction: column; gap: 0.5rem; text-decoration: none; }
        .app-card-link-area:hover { text-decoration: none; }
        .app-card-name { font-size: 1.1rem; font-weight: 700; color: var(--navy); }
        .app-card-link { font-size: 0.85rem; color: var(--blue); }
        .app-card-children { font-size: 0.78rem; color: var(--gray-500); padding-top: 0.4rem; border-top: 1px solid var(--gray-100); }
        .app-card-children a { color: var(--blue); }
        .empty-state { max-width: 960px; margin: 3rem auto; padding: 0 1.5rem; color: var(--gray-500); }
        .empty-state code { display: inline-block; margin-top: 0.5rem; }
        """;
}
