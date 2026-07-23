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

    // A page's own <h1>/<h2> heading is the product-facing name a real user actually sees — often more
    // meaningful than the component's technical file/export name (e.g. a screen named "ContractMasters" in
    // code, but headed "Contract Masters" on screen — real, generic example this was written for, not a
    // one-off). Deliberately narrow: only fires when a file has EXACTLY one h1/h2 (no ambiguity about which
    // heading belongs to the default-exported component) and the heading is plain text, not a JS expression
    // (`{title}`) — quoting an interpolated value as if it were a literal fact would be a real inaccuracy.
    private static readonly Regex Heading = new(@"<h[12][^>]*>\s*([^<{}]{1,80}?)\s*</h[12]>", RegexOptions.Compiled);

    // Detects a conditional keyed on emptiness (e.g. `{items.length === 0 ? <EmptyState/> : <Table/>}`) and
    // captures what the empty branch actually shows. Deliberately narrow: only the `=== 0 ? <empty-branch`
    // shape (the empty branch is the ternary's TRUE branch, immediately after `?`) — not the reversed
    // `length > 0 ? <loaded> : <empty>` form. Correctly isolating content after a `:` inside arbitrary,
    // possibly-nested JSX is a real balanced-parsing problem a regex can't safely solve, and guessing wrong
    // would mislabel the loaded-state content as the empty state — worse than not capturing it at all.
    // Only the first plain-text JSX content within a bounded window after the branch starts is captured —
    // no deep traversal into nested logic, same discipline as the existing Wave C detectors.
    private static readonly Regex EmptyStateBranch = new(@"\w+\.length\s*===\s*0\s*\?", RegexOptions.Compiled);
    private static readonly Regex FirstJsxText = new(@">\s*([A-Za-z][^<>{}\r\n]{2,80}?)\s*<", RegexOptions.Compiled);
    private const int EmptyStateWindow = 300;

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

            MatchCollection headings = Heading.Matches(file.Text);
            string? displayName = headings.Count == 1 && headings[0].Groups[1].Value.Trim() is { Length: > 0 } h ? h : null;

            MatchCollection emptyStateBranches = EmptyStateBranch.Matches(file.Text);
            string? emptyStateLabel = null;
            if (emptyStateBranches.Count == 1)
            {
                int start = emptyStateBranches[0].Index + emptyStateBranches[0].Length;
                int windowLen = Math.Min(EmptyStateWindow, file.Text.Length - start);
                if (windowLen > 0 && FirstJsxText.Match(file.Text, start, windowLen) is { Success: true } et)
                    emptyStateLabel = et.Groups[1].Value.Trim();
            }

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
                if (isDefault && displayName is not null) props.Add(("displayName", displayName));
                if (isDefault && emptyStateLabel is not null) props.Add(("emptyStateLabel", emptyStateLabel));

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

    // createBrowserRouter/createHashRouter([{ path, element, children }, ...]) — the object/data-router
    // config style, invisible to the JSX <Route> regex above since there's no JSX at all. Each entry is
    // matched up to the next "path:" or the end of the array, mirroring how RouteBlock bounds a JSX <Route>
    // up to the next sibling — a flat scan, not a real nested-object parser, so parent/child route nesting
    // isn't reconstructed, but every declared route (at any depth) is still found and never silently missed.
    private static readonly Regex ObjectRouterCall = new(@"create(?:Browser|Hash)Router\s*\(", RegexOptions.Compiled);
    private static readonly Regex ObjectRouteEntry =
        new(@"path\s*:\s*[""']([^""']*)[""'][\s\S]*?(?=path\s*:\s*[""']|\]\s*\)|\]\s*;|$)", RegexOptions.Compiled);

    public string Name => "react-routes";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (file.Text.Contains("react-router-dom", StringComparison.Ordinal))
            {
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

            if (ObjectRouterCall.IsMatch(file.Text))
            {
                foreach (Match m in ObjectRouteEntry.Matches(file.Text))
                {
                    string path = m.Groups[1].Value;
                    string display = string.IsNullOrEmpty(path) ? "(index)" : path;
                    bool guarded = m.Value.Contains("PrivateRoute", StringComparison.Ordinal)
                        || m.Value.Contains("ProtectedRoute", StringComparison.Ordinal);

                    KnowledgeIdentity routeId = context.NodeId(Rx.Seg("route", display));
                    Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), $"path:{display}");
                    sink.Add(NodeDiscovery.Create(routeId, NodeKind.From("Route"), new[] { ev }, Confidence.From(0.75),
                        Rx.Props(("path", display), ("protected", guarded ? "yes" : "no"))));

                    foreach (string rendered in Rx.JsxTag.Matches(m.Value).Select(t => t.Groups[1].Value).Distinct())
                    {
                        Evidence renderEv = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), $"{display}->{rendered}");
                        sink.Add(RelationshipDiscovery.Create(RelationshipType.From("RENDERS"),
                            routeId, context.NodeId(Rx.Seg("component", rendered)), new[] { renderEv }, Confidence.From(0.6)));
                    }
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
/// Detects a role→default-route mapping — a function like <c>getDefaultRoute(role)</c> that switches on a
/// role value and returns each role's landing page. This is a distinct fact from a role GATING a route
/// (see ReactRoleGateAnalyzer): it says where a role is sent BY DEFAULT, not what a role is restricted
/// from — hence its own relationship type, ROUTES_TO, rather than reusing GATES.
/// </summary>
public sealed class ReactDefaultRouteAnalyzer : IAnalyzer
{
    private static readonly Regex DefaultRouteFunctionStart =
        new(@"\b(?:function\s+)?(get\w*default\w*route\w*|default\w*route\w*for\w*)\s*[:=]?\s*(?:\([^)]*\)|\w+)?\s*(?:=>)?\s*\{", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RoleRouteCase =
        new(@"case\s+(?:ROLES\.)?(\w+)\s*:[\s\S]{0,120}?return\s+[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled);

    public string Name => "react-default-routes";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            foreach (Match start in DefaultRouteFunctionStart.Matches(file.Text))
            {
                int bodyStart = start.Index + start.Length - 1;
                int bodyEnd = FindMatchingBrace(file.Text, bodyStart);
                if (bodyEnd < 0) continue;
                string body = file.Text[bodyStart..bodyEnd];

                foreach (Match m in RoleRouteCase.Matches(body).DistinctBy(m => m.Groups[1].Value))
                {
                    string role = m.Groups[1].Value;
                    string route = m.Groups[2].Value;
                    int absoluteIndex = bodyStart + m.Index;
                    Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, absoluteIndex), $"{role}->{route}");
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("ROUTES_TO"),
                        context.NodeId(Rx.Seg("role", role)), context.NodeId(Rx.Seg("route", route)), new[] { ev }, Confidence.From(0.65)));
                }
            }
        }

        return Task.CompletedTask;
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        int depth = 0;
        for (int i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { depth--; if (depth == 0) return i + 1; }
        }

        return -1;
    }
}

/// <summary>
/// Detects role/permission-gated UI — any component whose code calls a role-check-shaped function (a hook
/// like <c>useUserRole</c>/<c>useUserPermissions</c>, or a helper like <c>hasAnyRole</c>/<c>hasRole</c>/
/// <c>hasPermission</c>) is treated as gating whatever it renders. One detector covers both a small
/// button-level guard (RoleGuard-style, wrapping one element) and a whole-app layout gate (Next.js
/// app-guard-style, wrapping the entire page) — the same underlying pattern, just wrapping a different
/// amount of markup. Emits Role→UIComponent (GATES), since a Role node on its own (see ReactRoleAnalyzer)
/// has no edge today to what it actually restricts.
/// </summary>
public sealed class ReactRoleGateAnalyzer : IAnalyzer
{
    private static readonly Regex RoleCheckCall =
        new(@"\b(?:use\w*Role\w*|use\w*Permission\w*|hasAnyRole|hasRole|hasPermission)\s*\(", RegexOptions.Compiled);
    // Only genuinely role/permission-shaped tokens — ROLES.X (the established vocabulary convention, see
    // ReactRouteAnalyzer) or a quoted "GROUP.NAME" literal (e.g. "ENTITY.ADMIN") — never a bare word, to
    // avoid matching arbitrary unrelated string constants that happen to sit in the same file.
    private static readonly Regex RoleToken = new(@"ROLES\.(\w+)|[""']([A-Z][A-Z_]*\.[A-Z][A-Z_]*)[""']", RegexOptions.Compiled);
    // A file whose own name IS the hook/utility (useUserRole.ts, RoleUtils.ts, PermissionHelpers.ts) defines
    // the role-check logic rather than consuming it to gate a component — it will always match RoleCheckCall
    // (it names the very function being matched) and every such GATES relationship was observed to be
    // rejected by Validation in practice ("neither endpoint is in Knowledge"), since there's no real
    // UIComponent node with that name. Excluded up front rather than left to be silently discarded downstream.
    private static readonly Regex DefinitionFileName = new(@"^use[A-Z]|Utils?$|Helpers?$|Service$", RegexOptions.Compiled);

    public string Name => "react-role-gates";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!RoleCheckCall.IsMatch(file.Text)) continue;

            string component = Path.GetFileNameWithoutExtension(file.Path);
            if (DefinitionFileName.IsMatch(component)) continue;

            var roles = RoleToken.Matches(file.Text)
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Distinct().Take(10).ToList();
            if (roles.Count == 0) continue;

            KnowledgeIdentity componentId = context.NodeId(Rx.Seg("component", component));
            foreach (string role in roles)
            {
                Evidence ev = context.Evidence(file.Path, 1, $"{component}:{role}");
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("GATES"),
                    context.NodeId(Rx.Seg("role", role)), componentId, new[] { ev }, Confidence.From(0.6)));
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
/// Detects analytics/KPI dashboard components — a chart-library import (recharts, Chart.js via
/// react-chartjs-2, or the lower-level chart.js package itself) is a strong, low-noise signal that a
/// component's whole purpose is presenting an analytics visualization, not just "a UIComponent". Without
/// this, dashboard components are indistinguishable from any other component in the generated docs, losing
/// the "this app has an analytics/reporting capability" fact entirely.
/// </summary>
public sealed class ReactAnalyticsChartAnalyzer : IAnalyzer
{
    private static readonly (string Import, string Library)[] ChartLibraries =
    {
        ("recharts", "recharts"),
        ("react-chartjs-2", "Chart.js"),
        ("chart.js", "Chart.js"),
    };

    public string Name => "react-analytics-charts";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            (string Import, string Library)? match = ChartLibraries.FirstOrDefault(l => file.Text.Contains($"\"{l.Import}\"", StringComparison.Ordinal) || file.Text.Contains($"'{l.Import}'", StringComparison.Ordinal));
            if (match is null) continue;

            string component = Path.GetFileNameWithoutExtension(file.Path);
            Evidence ev = context.Evidence(file.Path, 1, component);
            // Emitted alongside whatever ReactComponentAnalyzer already found for this file (a generic
            // UIComponent), not instead of it — this adds the analytics-specific fact on top.
            sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("analyticschart", component)),
                NodeKind.From("AnalyticsChart"), new[] { ev }, Confidence.From(0.8),
                Rx.Props(("name", component), ("library", match.Value.Library))));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects client-side filters via their <c>useState</c> declaration and classifies WHAT they actually are,
/// rather than only grounding that a filter-shaped variable exists — the earlier version treated every
/// state variable whose name contained "filter" identically, which produced documentation that read like a
/// raw variable dump instead of a description of what the screen lets a user do. Classification is purely
/// structural (never a guess at business meaning):
///  - a <c>...Open</c> suffix is UI chrome (a dropdown/panel's expanded state), not a filter criterion at
///    all, and is dropped entirely — nobody documenting the system needs a dropdown's open/closed state.
///  - a <c>selected...</c> prefix or plural <c>...Filters</c> suffix is a multi-select criterion.
///  - a <c>...Tab</c> suffix is a tab-driven criterion.
///  - everything else is a single-value criterion.
/// It then traces actual USAGE in the same file — a <c>.filter(x => x.field === state)</c> predicate, a
/// query-string/URLSearchParams builder, or an object-literal key the state is assigned to — to ground WHAT
/// field or query parameter it filters against (<c>targetField</c>). When no usage site can be found the
/// node is still emitted (the state genuinely exists), just without <c>targetField</c> — never guessed from
/// the variable name alone.
/// </summary>
public sealed class ReactFilterAnalyzer : IAnalyzer
{
    private static readonly Regex FilterState =
        new(@"const\s*\[\s*(\w*[Ff]ilters?\w*)\s*,\s*set\w+\s*\]\s*=\s*useState", RegexOptions.Compiled);
    // A tab-switch feeding a live view (e.g. "Under Allocated" / "Over Allocated" / "0% Allocation") is the
    // same underlying capability as a filter — narrowing what's shown by a criterion the user picks — just
    // presented as tabs instead of a filter control. Only counted when it also drives a fetch (a useEffect
    // dependency array referencing the tab state, or an ApiCall inside the same handler), so an ordinary
    // "which panel is open" UI tab doesn't get mistaken for a data-filtering one.
    private static readonly Regex TabState =
        new(@"const\s*\[\s*(\w*(?:[Tt]ab|[Vv]iew)\w*)\s*,\s*set\w+\s*\]\s*=\s*useState", RegexOptions.Compiled);
    private static readonly Regex FetchSignal = new(@"\b(?:fetch|axios|api\w*\.(?:get|post))\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "react-filters";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            string component = Path.GetFileNameWithoutExtension(file.Path);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in FilterState.Matches(file.Text).DistinctBy(m => m.Groups[1].Value))
            {
                string field = m.Groups[1].Value;
                seen.Add(field);
                string? kind = ClassifyShape(field);
                if (kind is null) continue; // "...Open" toggle-shaped UI chrome, not a filter criterion

                var props = new List<(string, string)> { ("name", field), ("component", component), ("kind", kind) };
                if (FindTargetField(file.Text, field) is { Length: > 0 } target) props.Add(("targetField", target));

                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), field);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("filter", $"{component}:{field}")),
                    NodeKind.From("Filter"), new[] { ev }, Confidence.From(0.75), Rx.Props(props.ToArray())));
            }

            if (!FetchSignal.IsMatch(file.Text)) continue;
            foreach (Match m in TabState.Matches(file.Text).DistinctBy(m => m.Groups[1].Value))
            {
                string field = m.Groups[1].Value;
                if (!seen.Add(field)) continue; // already captured (and classified) via FilterState above
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), field);
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("filter", $"{component}:{field}")),
                    NodeKind.From("Filter"), new[] { ev }, Confidence.From(0.6),
                    Rx.Props(("name", field), ("component", component), ("kind", "view-tab"))));
            }
        }

        return Task.CompletedTask;
    }

    // Structural classification from the identifier's own naming convention — never a guess at business
    // meaning, only at shape. Returns null for "...Open" toggle state, which isn't a filter criterion.
    internal static string? ClassifyShape(string name)
    {
        if (name.EndsWith("Open", StringComparison.Ordinal)) return null;
        if (name.StartsWith("selected", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Filters", StringComparison.Ordinal)) return "multi-select";
        if (name.EndsWith("Tab", StringComparison.Ordinal)) return "tab";
        return "single-value";
    }

    // Traces the state variable to a usage site that reveals what it filters. Tried in order: a .filter()
    // predicate comparing an object property (either comparison direction), a query-string interpolation,
    // a URLSearchParams-style builder call, or an object-literal key it's assigned to. File-wide regex
    // search rather than a scoped AST walk — same heuristic precision level as the rest of this analyzer —
    // so on a file with multiple unrelated .filter() calls this can in principle attribute the wrong field;
    // accepted as a disclosed limitation rather than building a full expression-tree walker for it.
    internal static string? FindTargetField(string text, string name)
    {
        string n = Regex.Escape(name);

        Match m = Regex.Match(text, @"\.filter\s*\(\s*\w+\s*=>[\s\S]{0,120}?\.(\w+)\s*(?:===?|!==?)\s*" + n + @"\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"\.filter\s*\(\s*\w+\s*=>[\s\S]{0,120}?" + n + @"\s*(?:===?|!==?)\s*\w+\.(\w+)\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"[?&]([\w-]+)=\$\{" + n + @"\}");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"\.(?:append|set)\s*\(\s*['""`]([\w-]+)['""`]\s*,\s*" + n + @"\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"(\w+)\s*:\s*" + n + @"\s*[,}]");
        if (m.Success) return m.Groups[1].Value;

        return null;
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
    // A second, reinforcing signal on the SAME node (not a new node/kind): a background job whose progress
    // is polled — setInterval, a poll*-named identifier, or the job-status literals a status-polling loop
    // typically switches on. Without this, a bulk-upload's real behavior (submit, then watch it finish
    // asynchronously) reads identically to a synchronous one-shot import.
    private static readonly Regex PollingSignal =
        new(@"\bsetInterval\s*\(|\bpoll\w*\s*\(|[""'](?:Pending|Processing)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "react-importexport";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            bool polls = PollingSignal.IsMatch(file.Text);
            foreach (Match m in Handler.Matches(file.Text))
            {
                string name = m.Groups[1].Value;
                string kind = m.Groups[2].Value;
                Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, m.Index), name);
                var props = new List<(string, string)> { ("name", name), ("kind", kind) };
                if (polls) props.Add(("async", "polls job status"));
                sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("importexport", name)),
                    NodeKind.From("ImportExport"), new[] { ev }, Confidence.From(0.8), Rx.Props(props.ToArray())));
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

    // Broader manual-validation detection, scoped to a validation-shaped function's body only — a real
    // form used a variable named `e` (not errors/newErrors) for the exact same pattern, which the regex
    // above misses entirely since it's anchored to those two names. Widening the name match to "any
    // identifier" file-wide would flag ordinary object-property assignment anywhere (config.name = "x"),
    // so this stays bounded to inside a function whose own name reads as validation logic
    // (validate*/handleSubmit*/checkForm*/onSubmit*) — the same guard the plan calls for.
    private static readonly Regex ValidationFunctionStart =
        new(@"\b(?:function\s+)?(validate\w*|handleSubmit\w*|checkForm\w*|onSubmit\w*)\s*[:=]?\s*(?:\([^)]*\)|\w+)?\s*(?:=>)?\s*\{", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FieldErrorAssignment = new(@"\b\w+\.(\w+)\s*=\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled);

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

            foreach (Match start in ValidationFunctionStart.Matches(file.Text))
            {
                int bodyStart = start.Index + start.Length - 1; // the '{' itself
                int bodyEnd = FindMatchingBrace(file.Text, bodyStart);
                if (bodyEnd < 0) continue;
                string body = file.Text[bodyStart..bodyEnd];

                foreach (Match m in FieldErrorAssignment.Matches(body).DistinctBy(m => m.Groups[1].Value))
                {
                    string field = m.Groups[1].Value;
                    string message = m.Groups[2].Value;
                    int absoluteIndex = bodyStart + m.Index;
                    Evidence ev = context.Evidence(file.Path, Rx.LineAt(file.Text, absoluteIndex), field);
                    sink.Add(NodeDiscovery.Create(context.NodeId(Rx.Seg("formfield", $"{component}:{field}")),
                        NodeKind.From("FormField"), new[] { ev }, Confidence.From(0.65),
                        Rx.Props(("name", field), ("form", component), ("validation", message))));
                }
            }
        }

        return Task.CompletedTask;
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        int depth = 0;
        for (int i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { depth--; if (depth == 0) return i + 1; }
        }

        return -1;
    }
}
