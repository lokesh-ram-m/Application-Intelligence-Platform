using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;
using Aip.Plugins.React;

namespace Aip.Plugins.NextJs;

/// <summary>
/// Derives Next.js routes from the file system, the way Next does. App Router: <c>app/&lt;segments&gt;/page.tsx</c>
/// → <c>/&lt;segments&gt;</c> (route groups <c>(x)</c> are stripped, dynamic <c>[id]</c> → <c>{id}</c>,
/// <c>[...slug]</c> → <c>{slug}</c>); <c>app/**/route.ts</c> is an API route. Pages Router: <c>pages/foo.tsx</c>
/// → <c>/foo</c>, <c>pages/index.tsx</c> → <c>/</c>, <c>pages/api/**</c> are API routes.
/// </summary>
public sealed class NextRouteAnalyzer : IAnalyzer
{
    public string Name => "next-routes";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        string root = Path.GetDirectoryName(context.Artifact.Path) ?? context.Artifact.Path;

        foreach (TsFile file in model.Files)
        {
            string rel = Path.GetRelativePath(root, file.Path).Replace('\\', '/');
            var segs = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 2) continue;
            string fileName = Path.GetFileNameWithoutExtension(segs[^1]).ToLowerInvariant();

            string container = segs[0];
            bool appRouter = container is "app" or "src" && (container == "app" || (segs.Length > 1 && segs[1] == "app"));
            int start = container == "app" ? 1 : (segs.Length > 1 && segs[1] == "app" ? 2 : -1);
            bool pagesRouter = container == "pages" || (segs.Length > 1 && segs[1] == "pages");

            if (start >= 0 && appRouter)
            {
                if (fileName is "page") EmitRoute(context, sink, file, Route(segs[start..^1]), "page");
                else if (fileName is "route") EmitRoute(context, sink, file, Route(segs[start..^1]), "api-route");
                else if (fileName is "layout") EmitRoute(context, sink, file, Route(segs[start..^1]), "layout");
            }
            else if (pagesRouter)
            {
                int pstart = container == "pages" ? 1 : 2;
                if (fileName is "_app" or "_document") continue;
                var routeSegs = segs[pstart..^1].Append(fileName == "index" ? "" : fileName).Where(s => s.Length > 0).ToArray();
                bool isApi = routeSegs.Length > 0 && routeSegs[0] == "api";
                EmitRoute(context, sink, file, Route(routeSegs), isApi ? "api-route" : "page");
            }
        }

        return Task.CompletedTask;
    }

    // Build a URL path from Next path segments: strip route groups (x), map dynamic [id] -> {id}, [...s] -> {s}.
    private static string Route(IReadOnlyList<string> segments)
    {
        var parts = new List<string>();
        foreach (string seg in segments)
        {
            if (seg.StartsWith('(') && seg.EndsWith(')')) continue;            // route group — not in the URL
            if (seg.StartsWith('[') && seg.EndsWith(']'))
                parts.Add("{" + seg.Trim('[', ']').TrimStart('.') + "}");       // [id] / [...slug] -> {id}/{slug}
            else parts.Add(seg);
        }

        return "/" + string.Join('/', parts);
    }

    private static void EmitRoute(IAnalysisContext context, IDiscoverySink sink, TsFile file, string path, string kind)
    {
        string display = path.Length == 0 ? "/" : path;
        Evidence ev = context.Evidence(file.Path, 1, $"{kind}:{display}");
        sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("route", $"{kind}:{display}")),
            NodeKind.From("Route"), new[] { ev }, Confidence.From(0.9), Rx.Props(("path", display), ("type", kind))));
    }
}
