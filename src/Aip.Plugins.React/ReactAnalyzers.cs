using System.Text.RegularExpressions;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;

namespace Aip.Plugins.React;

/// <summary>Shared helpers for the React/Next heuristic analyzers (regex reading of the TypeScript model).</summary>
public static class Rx
{
    public static IdentitySegment Seg(string kind, string value) => IdentitySegment.Seg(kind, value);

    public static int LineAt(string text, int index) => TsFile.LineAt(text, index);

    public static Dictionary<string, string> Props(params (string Key, string Value)[] pairs) => PropertyBag.Props(pairs);

    private static readonly Regex UrlField =
        new(@"(?:const|let|var)\s+(\w+)\s*=\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled);
    private static readonly Regex HostRx = new(@"^https?://[^/]+", RegexOptions.Compiled);

    /// <summary>Collect string consts that look like API base URLs, for interpolation into fetch/axios calls.</summary>
    public static Dictionary<string, string> UrlBases(string text)
    {
        var bases = new Dictionary<string, string>();
        foreach (Match m in UrlField.Matches(text))
        {
            string val = m.Groups[2].Value;
            if (val.Contains("/api") || val.StartsWith("http") || val.StartsWith('/'))
                bases[m.Groups[1].Value] = val;
        }

        return bases;
    }

    private static readonly Regex StaticAsset =
        new(@"\.(css|scss|png|jpe?g|gif|svg|ico|woff2?|ttf|eot|json|txt|map|mjs|js)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolve a fetch/axios URL literal to a backend path. Substitutes known base-URL consts and strips the
    /// host. When the base is an unresolvable env var (e.g. <c>${API_BASE_URL}</c> = <c>process.env.…</c>),
    /// the leading interpolation is dropped so the call is still documented by its relative path.
    /// </summary>
    public static string? ResolveUrl(string raw, IReadOnlyDictionary<string, string> bases)
    {
        string s = raw.Trim().Trim('`', '\'', '"').Trim();
        foreach ((string field, string value) in bases)
            s = s.Replace("${" + field + "}", value);
        s = HostRx.Replace(s, "");
        s = Regex.Replace(s, @"^\$\{[^}]*\}", "");       // drop an unresolved leading base like ${API_BASE_URL}
        if (!s.StartsWith('/')) s = "/" + s;

        // Keep only real backend-looking paths: at least one literal segment, not a static asset.
        if (StaticAsset.IsMatch(s)) return null;
        bool hasLiteralSegment = s.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(seg => seg.Length > 0 && seg[0] is not ('$' or '{' or ':') && char.IsLetter(seg[0]));

        return hasLiteralSegment ? s : null;
    }

    // Extract prop names from a component's first parameter: `{ a, b }` or `props: XxxProps`.
    public static string Props0(string paramList)
    {
        Match destructured = Regex.Match(paramList, @"\{([^}]*)\}");
        if (destructured.Success)
            return string.Join(", ", destructured.Groups[1].Value
                .Split(',').Select(p => p.Split(':')[0].Trim()).Where(p => p.Length > 0 && p != "..."));
        Match typed = Regex.Match(paramList, @":\s*(\w+)");

        return typed.Success ? typed.Groups[1].Value : "";
    }

    // A JSX opening tag starting with an uppercase letter is a component reference by React's own
    // convention, never a DOM element — shared by anything that needs to know "what does this JSX render".
    // Also consumes a trailing member-expression suffix (<UserContext.Provider>, <Menu.Item>) so those tags
    // still match at all — the capture group stays just the leading identifier, e.g. "UserContext".
    public static readonly Regex JsxTag = new(@"<([A-Z][A-Za-z0-9]*)(?:\.[A-Za-z][A-Za-z0-9]*)*(?![.\w])(?=[\s/>])", RegexOptions.Compiled);

    private static readonly Regex OwnerDefaultFn = new(@"export\s+default\s+function\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex OwnerDefaultArrowConst = new(@"export\s+default\s+([A-Z]\w+)\s*;", RegexOptions.Compiled);
    private static readonly Regex OwnerArrowConst =
        new(@"export\s+const\s+([A-Z]\w+)\s*(?::[^=]+)?=\s*(?:React\.)?(?:memo\(|forwardRef(?:<[^>]*>)?\()?\s*\([^)]*\)\s*(?::[^=]+)?=>", RegexOptions.Compiled);

    /// <summary>The file's primary exported component name (prefers a default export), or null if none is found —
    /// the same "which component owns this file" heuristic used for composition, filters, and form fields.</summary>
    public static string? OwnerComponent(string text) =>
        OwnerDefaultFn.Match(text) is { Success: true } d ? d.Groups[1].Value
        : OwnerDefaultArrowConst.Match(text) is { Success: true } da ? da.Groups[1].Value
        : OwnerArrowConst.Match(text) is { Success: true } a ? a.Groups[1].Value
        : null;
}

/// <summary>
/// Detects React components (default/named function and arrow components) with their props, the hooks they
/// use, and whether they are client or server components ("use client").
/// </summary>
public sealed class ReactComponentAnalyzer : IAnalyzer
{
    private static readonly Regex DefaultFn = new(@"export\s+default\s+function\s+(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex NamedFn = new(@"export\s+function\s+([A-Z]\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex ArrowConst = new(@"export\s+const\s+([A-Z]\w+)\s*(?::[^=]+)?=\s*(?:React\.)?(?:memo\(|forwardRef(?:<[^>]*>)?\()?\s*\(([^)]*)\)\s*(?::[^=]+)?=>", RegexOptions.Compiled);
    private static readonly Regex HookUse = new(@"\b(use[A-Z]\w*)\s*\(", RegexOptions.Compiled);

    // React's own built-in hooks — excluded from the component→hook USES edge so it captures custom hooks
    // (the interesting, app-specific fact already grounded by ReactHookAnalyzer) rather than noise from
    // every component that merely calls useState/useEffect.
    private static readonly HashSet<string> BuiltinHooks = new(StringComparer.Ordinal)
    {
        "useState", "useEffect", "useContext", "useMemo", "useCallback", "useRef", "useReducer",
        "useLayoutEffect", "useImperativeHandle", "useDebugValue", "useTransition", "useDeferredValue",
        "useId", "useSyncExternalStore", "useInsertionEffect", "useOptimistic", "useActionState", "useFormStatus",
    };

    public string Name => "react-components";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!file.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) &&
                !file.Path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)) continue;

            bool isClient = file.Text.Contains("\"use client\"") || file.Text.Contains("'use client'");
            List<string> hookNames = HookUse.Matches(file.Text).Select(m => m.Groups[1].Value).Distinct().ToList();
            string hooks = string.Join(", ", hookNames.Take(12));
            List<string> customHooks = hookNames.Where(h => !BuiltinHooks.Contains(h)).ToList();
            string fileKind = FileKind(file.Path);

            foreach ((Match m, bool isDefault) in DefaultFn.Matches(file.Text).Select(m => (m, true))
                         .Concat(NamedFn.Matches(file.Text).Select(m => (m, false)))
                         .Concat(ArrowConst.Matches(file.Text).Select(m => (m, false))))
            {
                string name = m.Groups[1].Value;
                if (name.StartsWith("use", StringComparison.Ordinal)) continue;   // a hook, handled elsewhere
                var props = new List<(string, string)>
                {
                    ("name", name),
                    ("kind", isDefault ? fileKind : "component"),
                    ("rendering", isClient ? "client" : "server"),
                };
                string p = Rx.Props0(m.Groups[2].Value);
                if (p.Length > 0) props.Add(("props", p));
                if (hooks.Length > 0) props.Add(("hooks", hooks));

                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), name);
                KnowledgeIdentity componentId = context.NodeId(Rx.Seg("component", name));
                sink.Add(NodeDiscovery.Create(componentId,
                    NodeKind.From("UIComponent"), new[] { ev }, Confidence.From(0.85), Rx.Props(props.ToArray())));

                foreach (string hook in customHooks)
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("USES"),
                        componentId, context.NodeId(Rx.Seg("hook", hook)), new[] { ev }, Confidence.From(0.7)));
            }
        }

        return Task.CompletedTask;
    }

    private static string FileKind(string path)
    {
        string f = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

        return f switch { "page" => "page", "layout" => "layout", "loading" => "loading", "error" => "error", _ => "component" };
    }
}

/// <summary>Detects custom React hooks (functions named use*).</summary>
public sealed class ReactHookAnalyzer : IAnalyzer
{
    private static readonly Regex HookDecl =
        new(@"export\s+(?:default\s+)?(?:function\s+(use[A-Z]\w*)|const\s+(use[A-Z]\w*)\s*=)", RegexOptions.Compiled);

    public string Name => "react-hooks";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
            foreach (Match m in HookDecl.Matches(file.Text))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), name);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("hook", name)),
                    NodeKind.From("Hook"), new[] { ev }, Confidence.From(0.85), Rx.Props(("name", name))));
            }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects fetch()/axios calls and the backend URLs they target (frontend→backend). Also links the call
/// back to its owning component via a CALLS relationship — without this, an ApiCall node floats free in
/// the graph with no way to know which screen makes it, which breaks any attempt to trace "what backend
/// endpoints does this role/route/page actually reach" (MAPS_TO alone only gets you from call to endpoint,
/// not from a screen to its calls).
/// </summary>
public sealed class ReactApiCallAnalyzer : IAnalyzer
{
    private static readonly Regex FetchCall =
        new(@"fetch\s*\(\s*[`'""]([^`'""]+)[`'""]\s*(?:,\s*\{[^}]*?method\s*:\s*[`'""](\w+)[`'""])?", RegexOptions.Compiled);
    private static readonly Regex AxiosCall =
        new(@"axios\s*\.\s*(get|post|put|delete|patch)\s*(?:<[^>]*>)?\s*\(\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Many apps don't call fetch/axios directly — they wrap it in a client (apiClient.get(...),
    // httpClient.post(...)). Scoped to identifiers containing "api"/"client"/"http" so this doesn't
    // false-positive on unrelated .get()/.delete() calls (e.g. Map.get(key)).
    private static readonly Regex WrappedClientCall =
        new(@"\b(\w*(?:api|client|http)\w*)\s*\.\s*(get|post|put|delete|patch|postFormData)\s*(?:<[^>]*>)?\s*\(\s*[`'""]([^`'""]+)[`'""]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "react-apicalls";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            var bases = Rx.UrlBases(file.Text);
            string? owner = Rx.OwnerComponent(file.Text);

            foreach (Match m in FetchCall.Matches(file.Text))
                Emit(context, sink, file, m, m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : "GET", m.Groups[1].Value, bases, owner);
            foreach (Match m in AxiosCall.Matches(file.Text))
                Emit(context, sink, file, m, m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value, bases, owner);
            foreach (Match m in WrappedClientCall.Matches(file.Text))
            {
                string verb = m.Groups[2].Value.Equals("postFormData", StringComparison.OrdinalIgnoreCase) ? "POST" : m.Groups[2].Value.ToUpperInvariant();
                Emit(context, sink, file, m, verb, m.Groups[3].Value, bases, owner);
            }
        }

        return Task.CompletedTask;
    }

    private static void Emit(IAnalysisContext context, IDiscoverySink sink, TsFile file, Match m, string verb, string rawUrl,
        IReadOnlyDictionary<string, string> bases, string? owner)
    {
        string? url = Rx.ResolveUrl(rawUrl, bases);
        if (url is null) return;
        KnowledgeIdentity apiCallId = context.NodeId(Rx.Seg("apicall", $"{verb} {url}"));
        Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), $"{verb} {url}");
        sink.Add(NodeDiscovery.Create(apiCallId, NodeKind.From("ApiCall"), new[] { ev }, Confidence.From(0.75), Rx.Props(("verb", verb), ("url", url))));

        if (owner is not null)
            sink.Add(RelationshipDiscovery.Create(RelationshipType.From("CALLS"),
                context.NodeId(Rx.Seg("component", owner)), apiCallId, new[] { ev }, Confidence.From(0.7)));
    }
}

/// <summary>Detects React context providers (createContext) and Zustand stores (create(...)).</summary>
public sealed class ReactContextAnalyzer : IAnalyzer
{
    private static readonly Regex Context =
        new(@"(?:const|let)\s+(\w+)\s*=\s*(?:React\.)?createContext", RegexOptions.Compiled);
    // Zustand's create<State>()(...) / create((set) => ...) idiom. Requiring "create" be immediately
    // followed by "(" or a generic argument list keeps this from matching unrelated *createXxx(...) calls
    // (createContext, createSlice, …), which all have more characters between "create" and the paren.
    private static readonly Regex ZustandStore =
        new(@"(?:const|let)\s+(\w+)\s*=\s*create(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    public string Name => "react-context";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            foreach (Match m in Context.Matches(file.Text)) Emit(context, sink, file, m, "context");
            foreach (Match m in ZustandStore.Matches(file.Text)) Emit(context, sink, file, m, "store");
        }

        return Task.CompletedTask;
    }

    private static void Emit(IAnalysisContext context, IDiscoverySink sink, TsFile file, Match m, string kind)
    {
        string name = m.Groups[1].Value;
        Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), name);
        sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("context", name)),
            NodeKind.From("Context"), new[] { ev }, Confidence.From(0.8), Rx.Props(("name", name), ("kind", kind))));
    }
}

/// <summary>
/// Detects react-router-dom routes declared as JSX (&lt;Route path="..." element={...} /&gt;) — the
/// react-router equivalent of the object-config style Angular/Next already detect. Each route's element
/// subtree is scanned for a role guard (e.g. &lt;RoleBasedRoute allowedRoles={[ROLES.ADMIN]}&gt;) so access
/// control is grounded per-route, not just declared once in the abstract. Also emits a RENDERS relationship
/// from the route to every component in its element subtree (the same relationship ReactCompositionAnalyzer
/// uses for component-to-component composition) — this is what lets a Role→Route→Page→...→ApiCall→Endpoint
/// reachability chain be walked deterministically later, instead of stopping at "this route exists."
/// </summary>
public sealed class ReactRouteAnalyzer : IAnalyzer
{
    private static readonly Regex RouteBlock =
        new(@"<Route\s+path\s*=\s*[""']([^""']*)[""'][\s\S]*?(?=<Route\b|</Routes>|$)", RegexOptions.Compiled);
    private static readonly Regex AllowedRoles =
        new(@"allowedRoles\s*=\s*\{\s*\[([^\]]*)\]\s*\}", RegexOptions.Compiled);
    private static readonly Regex RoleToken = new(@"(?:ROLES\.)?(\w+)", RegexOptions.Compiled);

    public string Name => "react-routes";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!file.Text.Contains("react-router-dom", StringComparison.Ordinal)) continue;

            foreach (Match m in RouteBlock.Matches(file.Text))
            {
                string path = m.Groups[1].Value;
                string display = string.IsNullOrEmpty(path) ? "(index)" : path;
                bool guarded = m.Value.Contains("ProtectedRoute", StringComparison.Ordinal);

                var props = new List<(string, string)> { ("path", display), ("protected", guarded ? "yes" : "no") };
                Match roles = AllowedRoles.Match(m.Value);
                if (roles.Success)
                {
                    string joined = string.Join(", ", RoleToken.Matches(roles.Groups[1].Value).Select(r => r.Groups[1].Value).Distinct());
                    if (joined.Length > 0) props.Add(("roles", joined));
                }

                KnowledgeIdentity routeId = context.NodeId(Rx.Seg("route", display));
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), $"path:{display}");
                sink.Add(NodeDiscovery.Create(routeId, NodeKind.From("Route"), new[] { ev }, Confidence.From(0.85), Rx.Props(props.ToArray())));

                foreach (string rendered in Rx.JsxTag.Matches(m.Value).Select(t => t.Groups[1].Value).Where(n => n != "Route").Distinct())
                {
                    Evidence renderEv = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), $"{display}->{rendered}");
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("RENDERS"),
                        routeId, context.NodeId(Rx.Seg("component", rendered)), new[] { renderEv }, Confidence.From(0.6)));
                }
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Detects the application's role vocabulary (e.g. <c>export const ROLES = { ADMIN: "...", ... }</c>).</summary>
public sealed class ReactRoleAnalyzer : IAnalyzer
{
    private static readonly Regex RolesBlock =
        new(@"export\s+const\s+ROLES\s*=\s*\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex RoleEntry =
        new(@"(\w+)\s*:\s*[""']([^""']+)[""']", RegexOptions.Compiled);

    public string Name => "react-roles";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            Match block = RolesBlock.Match(file.Text);
            if (!block.Success) continue;

            foreach (Match m in RoleEntry.Matches(block.Groups[1].Value))
            {
                string name = m.Groups[1].Value;
                string value = m.Groups[2].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, block.Index), name);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("role", name)),
                    NodeKind.From("Role"), new[] { ev }, Confidence.From(0.9), Rx.Props(("name", name), ("value", value))));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects data grids/tables and the columns they display — grounds "what a grid shows" instead of just
/// "a table exists". Two independent, technology-agnostic shapes: TanStack Table's <c>useReactTable</c>
/// with an <c>accessorKey</c> column config, and a plain HTML <c>&lt;table&gt;</c> with <c>&lt;th&gt;</c>
/// header cells (works for any hand-rolled table, any React app — not tied to a specific library).
/// </summary>
public sealed class ReactDataGridAnalyzer : IAnalyzer
{
    private static readonly Regex AccessorKey = new(@"accessorKey\s*:\s*[`'""](\w+)[`'""]", RegexOptions.Compiled);
    private static readonly Regex TableBlock = new(@"<table\b[\s\S]*?</table\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ThCell = new(@"<th\b[^>]*>([\s\S]*?)</th\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StripTags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public string Name => "react-datagrids";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            string component = Path.GetFileNameWithoutExtension(file.Path);
            bool usesReactTable = file.Text.Contains("useReactTable", StringComparison.Ordinal);

            if (usesReactTable)
            {
                List<string> columns = AccessorKey.Matches(file.Text).Select(m => m.Groups[1].Value).Distinct().ToList();
                if (columns.Count > 0)
                {
                    Evidence ev = context.Evidence(file.Path, 1, component);
                    sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("datagrid", component)),
                        NodeKind.From("DataGrid"), new[] { ev }, Confidence.From(0.85),
                        Rx.Props(("name", component), ("columns", string.Join(", ", columns)))));
                }
                continue;   // already grounded via its column config — don't also scan for a plain <table>
            }

            int tableIndex = 0;
            foreach (Match table in TableBlock.Matches(file.Text))
            {
                List<string> headers = ThCell.Matches(table.Value)
                    .Select(m => Whitespace.Replace(StripTags.Replace(m.Groups[1].Value, ""), " ").Trim())
                    .Where(h => h.Length > 0 && !h.StartsWith('{'))   // drop JSX-expression headers we can't resolve statically
                    .Distinct()
                    .ToList();
                if (headers.Count == 0) continue;

                tableIndex++;
                string name = tableIndex == 1 ? component : $"{component} (table {tableIndex})";
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, table.Index), name);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("datagrid", name)),
                    NodeKind.From("DataGrid"), new[] { ev }, Confidence.From(0.8),
                    Rx.Props(("name", name), ("columns", string.Join(", ", headers)))));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects client-side filters via their <c>useState</c> declaration — any state variable whose name
/// contains "filter" (e.g. <c>selectedStatusFilters</c>, <c>typeFilterOpen</c>) is grounded as a filter
/// the UI exposes, named after the file it lives in.
/// </summary>
public sealed class ReactFilterAnalyzer : IAnalyzer
{
    private static readonly Regex FilterState =
        new(@"const\s*\[\s*(\w*[Ff]ilters?\w*)\s*,\s*set\w+\s*\]\s*=\s*useState", RegexOptions.Compiled);

    public string Name => "react-filters";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            string component = Path.GetFileNameWithoutExtension(file.Path);
            foreach (Match m in FilterState.Matches(file.Text).DistinctBy(m => m.Groups[1].Value))
            {
                string field = m.Groups[1].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), field);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("filter", $"{component}:{field}")),
                    NodeKind.From("Filter"), new[] { ev }, Confidence.From(0.75),
                    Rx.Props(("name", field), ("component", component))));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects import/export/download/upload handlers (e.g. <c>handleExportCsv</c>) and file-picker inputs
/// (<c>&lt;input type="file"&gt;</c>) — the concrete data-movement capabilities a page offers.
/// </summary>
public sealed class ReactImportExportAnalyzer : IAnalyzer
{
    private static readonly Regex Handler =
        new(@"(?:const|function)\s+(handle(Import|Export|Download|Upload)\w*)\b", RegexOptions.Compiled);
    private static readonly Regex FileInput = new(@"type\s*=\s*[""']file[""']", RegexOptions.Compiled);

    public string Name => "react-importexport";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            foreach (Match m in Handler.Matches(file.Text))
            {
                string name = m.Groups[1].Value;
                string kind = m.Groups[2].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), name);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("importexport", name)),
                    NodeKind.From("ImportExport"), new[] { ev }, Confidence.From(0.8),
                    Rx.Props(("name", name), ("kind", kind))));
            }

            if (FileInput.IsMatch(file.Text) && !Handler.IsMatch(file.Text))
            {
                string component = Path.GetFileNameWithoutExtension(file.Path);
                Evidence ev = context.Evidence(file.Path, 1, component);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("importexport", $"{component}:file-input")),
                    NodeKind.From("ImportExport"), new[] { ev }, Confidence.From(0.6),
                    Rx.Props(("name", $"{component} file upload"), ("kind", "Upload"))));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects which child components a component/page renders, purely from React's own naming convention —
/// a JSX tag starting with an uppercase letter is a component reference, never a DOM element. This is
/// technology-agnostic (works for any React app, not any particular UI library) and is what lets a page
/// like a dashboard be linked to the charts/cards it actually composes, instead of every component
/// floating as an unconnected fact. Speculative by design: a RENDERS relationship whose target name
/// doesn't resolve to an actual detected component is dropped downstream (validation only keeps
/// relationships whose endpoints both exist), so over-matching here is harmless.
/// </summary>
public sealed class ReactCompositionAnalyzer : IAnalyzer
{
    public string Name => "react-composition";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!file.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) &&
                !file.Path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)) continue;

            string? owner = Rx.OwnerComponent(file.Text);
            if (owner is null) continue;

            KnowledgeIdentity ownerId = context.NodeId(Rx.Seg("component", owner));
            var children = Rx.JsxTag.Matches(file.Text).Select(m => m.Groups[1].Value).Where(n => n != owner).Distinct();

            foreach (string child in children)
            {
                Evidence ev = context.Evidence(file.Path, 1, $"{owner}->{child}");
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("RENDERS"),
                    ownerId, context.NodeId(Rx.Seg("component", child)), new[] { ev }, Confidence.From(0.6)));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects form fields and their validation. Covers two common shapes: react-hook-form's
/// <c>register('field', {...})</c>, and hand-rolled validation that assigns a message onto an errors
/// object (e.g. <c>newErrors.dueDate = "Due Date is required"</c>) — the latter doubles as the field's
/// human-readable validation rule, grounded verbatim rather than inferred.
/// </summary>
public sealed class ReactFormFieldAnalyzer : IAnalyzer
{
    private static readonly Regex RegisterField = new(@"register\s*\(\s*[`'""](\w+)[`'""]", RegexOptions.Compiled);
    private static readonly Regex ManualValidation =
        new(@"(?:newErrors|errors)\.(\w+)\s*=\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled);

    public string Name => "react-formfields";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            string component = Path.GetFileNameWithoutExtension(file.Path);

            foreach (Match m in RegisterField.Matches(file.Text).DistinctBy(m => m.Groups[1].Value))
            {
                string field = m.Groups[1].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), field);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("formfield", $"{component}:{field}")),
                    NodeKind.From("FormField"), new[] { ev }, Confidence.From(0.8),
                    Rx.Props(("name", field), ("form", component))));
            }

            foreach (Match m in ManualValidation.Matches(file.Text).DistinctBy(m => m.Groups[1].Value))
            {
                string field = m.Groups[1].Value;
                string message = m.Groups[2].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), field);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("formfield", $"{component}:{field}")),
                    NodeKind.From("FormField"), new[] { ev }, Confidence.From(0.8),
                    Rx.Props(("name", field), ("form", component), ("validation", message))));
            }
        }

        return Task.CompletedTask;
    }
}
