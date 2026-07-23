using System.Text.RegularExpressions;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.TypeScript;

namespace Aip.Plugins.Angular;

/// <summary>Shared helpers for the Angular analyzers (heuristic reading of the TypeScript model).</summary>
internal static class Ng
{
    public static IdentitySegment Seg(string kind, string value) => IdentitySegment.Seg(kind, value);

    public static int LineAt(string text, int index) => TsFile.LineAt(text, index);

    public static Dictionary<string, string> Props(params (string Key, string Value)[] pairs) => PropertyBag.Props(pairs);
}

/// <summary>Detects Angular components (@Component + class) and their selectors.</summary>
internal sealed class AngularComponentAnalyzer : IAnalyzer
{
    private static readonly Regex Component =
        new(@"@Component\s*\(([\s\S]*?)\)\s*export\s+class\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex Selector =
        new(@"selector\s*:\s*['""`]([^'""`]+)['""`]", RegexOptions.Compiled);
    // Matches the boolean literal specifically (word-boundary on both sides) so a same-named field on some
    // other object in the decorator body can't be mistaken for the standalone flag itself.
    private static readonly Regex Standalone =
        new(@"\bstandalone\s*:\s*true\b", RegexOptions.Compiled);

    public string Name => "angular-components";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            foreach (Match m in Component.Matches(file.Text))
            {
                string className = m.Groups[2].Value;
                Match sel = Selector.Match(m.Groups[1].Value);
                string selector = sel.Success ? sel.Groups[1].Value : "";
                var props = new List<(string, string)> { ("name", className), ("selector", selector) };
                if (Standalone.IsMatch(m.Groups[1].Value)) props.Add(("standalone", "true"));
                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), className);
                sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("component", className)),
                    NodeKind.From("UIComponent"), new[] { ev }, Confidence.From(0.9), Ng.Props(props.ToArray())));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects which child components a component's template renders — the Angular equivalent of React's
/// RENDERS relationship (see ReactCompositionAnalyzer). Angular templates live in a separate .html file
/// referenced via @Component's templateUrl, so this first builds a selector→class map from every
/// @Component in the workspace, then for each component with a resolvable templateUrl, scans its template
/// file for other known components' selectors used as custom-element tags (Angular selectors are
/// conventionally kebab-case, which is what distinguishes a component reference from a plain HTML tag).
/// Unlike React's uppercase-JSX-tag heuristic (which matches speculatively and lets Validation drop
/// unresolved targets), this only emits an edge once the tag is confirmed to match a real, declared
/// selector — so no speculative matching is needed here.
/// </summary>
internal sealed class AngularTemplateCompositionAnalyzer : IAnalyzer
{
    private static readonly Regex ComponentDecorator =
        new(@"@Component\s*\(([\s\S]*?)\)\s*export\s+class\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex Selector = new(@"selector\s*:\s*['""`]([^'""`]+)['""`]", RegexOptions.Compiled);
    private static readonly Regex TemplateUrl = new(@"templateUrl\s*:\s*['""`]([^'""`]+)['""`]", RegexOptions.Compiled);
    // A kebab-case custom-element opening tag (app-header, user-card, …) — Angular's own selector
    // convention, which is what separates a component reference from a plain HTML element.
    private static readonly Regex ElementTag = new(@"<([a-z][a-z0-9]*(?:-[a-z0-9]+)+)\b", RegexOptions.Compiled);

    public string Name => "angular-template-composition";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        var filesByPath = new Dictionary<string, TsFile>(StringComparer.OrdinalIgnoreCase);
        foreach (TsFile f in model.Files) filesByPath[Path.GetFullPath(f.Path)] = f;

        var selectorToClass = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TsFile file in model.Files)
            foreach (Match m in ComponentDecorator.Matches(file.Text))
            {
                Match sel = Selector.Match(m.Groups[1].Value);
                if (sel.Success) selectorToClass[sel.Groups[1].Value] = m.Groups[2].Value;
            }

        foreach (TsFile file in model.Files)
            foreach (Match m in ComponentDecorator.Matches(file.Text))
            {
                string className = m.Groups[2].Value;
                Match tpl = TemplateUrl.Match(m.Groups[1].Value);
                if (!tpl.Success) continue;

                string dir = Path.GetDirectoryName(file.Path) ?? "";
                string templatePath = Path.GetFullPath(Path.Combine(dir, tpl.Groups[1].Value));
                if (!filesByPath.TryGetValue(templatePath, out TsFile? template)) continue;

                KnowledgeIdentity ownerId = context.NodeId(Ng.Seg("component", className));
                foreach (string tag in ElementTag.Matches(template.Text).Select(t => t.Groups[1].Value).Distinct())
                {
                    if (!selectorToClass.TryGetValue(tag, out string? childClass) || childClass == className) continue;
                    Evidence ev = context.Evidence(template.Path, 1, $"{className}->{childClass}");
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("RENDERS"),
                        ownerId, context.NodeId(Ng.Seg("component", childClass)), new[] { ev }, Confidence.From(0.85)));
                }
            }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects Angular routes by structurally parsing route array objects (path/component/loadChildren/
/// children) instead of a bare <c>path:</c> scan — so a nested route (<c>children: [...]</c>) is emitted
/// with its full parent-joined path (e.g. <c>users/:id</c>, not just <c>:id</c>), a lazy-loaded feature
/// route (<c>loadChildren: () => import('./x/x.module').then(m => m.XModule)</c>) captures the imported
/// module, and an eagerly-routed one captures its <c>component</c>. A route object's OWN component/
/// loadChildren are matched only within the region of its body BEFORE its <c>children:</c> array starts, so
/// a nested child's own <c>component:</c>/<c>loadChildren:</c> can never be mistaken for the parent's.
/// </summary>
internal sealed class AngularRouteAnalyzer : IAnalyzer
{
    private static readonly Regex RoutesArrayStart =
        new(@"(?::\s*Routes\s*=|RouterModule\.for(?:Root|Child)\s*\()\s*\[", RegexOptions.Compiled);
    private static readonly Regex PathProp = new(@"\bpath\s*:\s*['""`]([^'""`]*)['""`]", RegexOptions.Compiled);
    private static readonly Regex ComponentProp = new(@"\bcomponent\s*:\s*(\w+)", RegexOptions.Compiled);
    private static readonly Regex LoadChildrenProp = new(
        @"loadChildren\s*:\s*\(\)\s*=>\s*import\(\s*['""`]([^'""`]+)['""`]\s*\)(?:\s*\.then\s*\(\s*\w+\s*=>\s*\w+\.(\w+)\s*\))?",
        RegexOptions.Compiled);
    private static readonly Regex ChildrenProp = new(@"\bchildren\s*:\s*\[", RegexOptions.Compiled);

    public string Name => "angular-routes";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!file.Path.Contains("routing", StringComparison.OrdinalIgnoreCase) &&
                !file.Text.Contains("Routes", StringComparison.Ordinal))
                continue;

            foreach (Match arrayStart in RoutesArrayStart.Matches(file.Text))
            {
                int openIndex = arrayStart.Index + arrayStart.Length - 1; // the '['
                int closeIndex = FindMatching(file.Text, openIndex, '[', ']');
                if (closeIndex < 0) continue;

                EmitRoutes(context, sink, file, openIndex + 1, closeIndex, parentPath: null);
            }
        }

        return Task.CompletedTask;
    }

    private static void EmitRoutes(IAnalysisContext context, IDiscoverySink sink, TsFile file, int arrayStart, int arrayEnd, string? parentPath)
    {
        string text = file.Text;
        int i = arrayStart;
        while (i < arrayEnd)
        {
            int brace = text.IndexOf('{', i, arrayEnd - i);
            if (brace < 0) break;
            int braceEnd = FindMatching(text, brace, '{', '}');
            if (braceEnd < 0) break;

            string body = text[brace..braceEnd];
            Match pathMatch = PathProp.Match(body);
            if (pathMatch.Success)
            {
                string path = pathMatch.Groups[1].Value;
                string full = string.IsNullOrEmpty(parentPath)
                    ? (string.IsNullOrEmpty(path) ? "(default)" : path)
                    : (string.IsNullOrEmpty(path) ? parentPath : $"{parentPath}/{path}");

                Match children = ChildrenProp.Match(body);
                // Restrict component/loadChildren matching to the object's OWN region — everything before
                // its `children:` array starts — so a nested child's `component:`/`loadChildren:` (inside
                // that array) can never be mistaken for this object's own. Angular route object convention
                // always lists a route's own properties before its nested `children`, so this is safe.
                string ownBody = children.Success ? body[..children.Index] : body;
                Match comp = ComponentProp.Match(ownBody);
                Match lazy = LoadChildrenProp.Match(ownBody);

                // A route object with `children` but neither its own `component` nor `loadChildren` is a
                // pathless grouping wrapper only — Angular resolves the actual rendered view from whichever
                // child matches, often an empty-path "index" child that lands on this exact same full path.
                // Skip emitting a node for the wrapper itself so it doesn't collide on identity with that
                // child's node; still recurse into the children below using its path prefix.
                bool isGroupingWrapperOnly = children.Success && !comp.Success && !lazy.Success;

                if (!isGroupingWrapperOnly)
                {
                    var props = new List<(string, string)> { ("path", full) };
                    if (comp.Success) props.Add(("component", comp.Groups[1].Value));
                    if (lazy.Success)
                    {
                        props.Add(("loadChildren", lazy.Groups[1].Value));
                        if (lazy.Groups[2].Success && lazy.Groups[2].Value.Length > 0)
                            props.Add(("loadChildrenExport", lazy.Groups[2].Value));
                    }

                    Evidence ev = context.Evidence(file.Path, Ng.LineAt(text, brace), $"path:{full}");
                    sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("route", full)),
                        NodeKind.From("Route"), new[] { ev }, Confidence.From(0.85), Ng.Props(props.ToArray())));
                }

                if (children.Success)
                {
                    int childArrayOpen = brace + children.Index + children.Length - 1; // the '['
                    int childArrayClose = FindMatching(text, childArrayOpen, '[', ']');
                    if (childArrayClose > 0) EmitRoutes(context, sink, file, childArrayOpen + 1, childArrayClose, full);
                }
            }

            i = braceEnd + 1;
        }
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) { depth--; if (depth == 0) return i; }
        }

        return -1;
    }
}

/// <summary>
/// Detects Angular services (@Injectable classes).</summary>
internal sealed class AngularServiceAnalyzer : IAnalyzer
{
    private static readonly Regex Injectable =
        new(@"@Injectable\s*\(([\s\S]*?)\)\s*export\s+class\s+(\w+)", RegexOptions.Compiled);

    public string Name => "angular-services";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
            foreach (Match m in Injectable.Matches(file.Text))
            {
                string name = m.Groups[2].Value;
                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), name);
                sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("service", name)),
                    NodeKind.From("UIService"), new[] { ev }, Confidence.From(0.9), Ng.Props(("name", name))));
            }

        return Task.CompletedTask;
    }
}

/// <summary>Detects Angular route guards, resolvers, and HTTP interceptors (class-based and functional).</summary>
internal sealed class AngularGuardAnalyzer : IAnalyzer
{
    private static readonly Regex ClassImpl =
        new(@"export\s+class\s+(\w+)\s+implements\s+([^{]+)\{", RegexOptions.Compiled);
    private static readonly Regex Functional =
        new(@"export\s+const\s+(\w+)\s*:\s*(CanActivate(?:Child)?Fn|CanMatchFn|CanDeactivateFn|HttpInterceptorFn|ResolveFn(?:<[^>]*>)?)", RegexOptions.Compiled);

    public string Name => "angular-guards";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            foreach (Match m in ClassImpl.Matches(file.Text))
            {
                string ifaces = m.Groups[2].Value;
                string? kind =
                    ifaces.Contains("HttpInterceptor") ? "Interceptor" :
                    ifaces.Contains("Resolve") ? "Resolver" :
                    (ifaces.Contains("CanActivate") || ifaces.Contains("CanDeactivate") || ifaces.Contains("CanMatch")) ? "Guard" : null;
                if (kind is not null) Emit(context, sink, file, m, m.Groups[1].Value, kind);
            }
            foreach (Match m in Functional.Matches(file.Text))
            {
                string ifaceMatch = m.Groups[2].Value;
                string kind = ifaceMatch.Contains("Interceptor") ? "Interceptor" : ifaceMatch.StartsWith("ResolveFn", StringComparison.Ordinal) ? "Resolver" : "Guard";
                Emit(context, sink, file, m, m.Groups[1].Value, kind);
            }
        }

        return Task.CompletedTask;
    }

    private static void Emit(IAnalysisContext context, IDiscoverySink sink, TsFile file, Match m, string name, string kind)
    {
        Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), name);
        sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg(kind.ToLowerInvariant(), name)),
            NodeKind.From(kind), new[] { ev }, Confidence.From(0.85), Ng.Props(("name", name))));
    }
}

/// <summary>
/// Wires a component/service to the Angular services it injects (constructor injection or the inject()
/// function). One class per file is the Angular convention, so dependencies attach to the file's primary class.
/// </summary>
internal sealed class AngularDependencyAnalyzer : IAnalyzer
{
    private static readonly Regex PrimaryClass = new(@"export\s+class\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex InjectFn = new(@"inject\s*\(\s*(\w+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex CtorParam = new(@"(?:private|public|protected|readonly)\s+\w+\s*:\s*(\w+)", RegexOptions.Compiled);

    // TS primitive/built-in type keywords the ctor-param regex can also pick up (e.g. `private id: string`) —
    // excluded so DEPENDS_ON only reports real dependency types, not scalar parameters.
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
        { "string", "number", "boolean", "any", "void", "unknown", "object", "never", "undefined", "null" };

    public string Name => "angular-dependencies";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            Match primary = PrimaryClass.Match(file.Text);
            if (!primary.Success) continue;
            string fromName = primary.Groups[1].Value;
            bool isComponent = file.Text.Contains("@Component");
            KnowledgeIdentity fromId = context.NodeId(Ng.Seg(isComponent ? "component" : "service", fromName));

            // Not restricted to *Service-suffixed types — that missed Router, ActivatedRoute, HttpClient,
            // an NgRx Store, or any custom facade/repository dependency that doesn't follow the naming
            // convention, even though the same regexes already capture their type name. Framework types that
            // aren't part of the analyzed graph are still proposed here and resolved (or grounded as an
            // External node) by Validation, same as every other relationship — not dropped at the source.
            var deps = InjectFn.Matches(file.Text).Select(m => m.Groups[1].Value)
                .Concat(CtorParam.Matches(file.Text).Select(m => m.Groups[1].Value))
                .Where(t => !PrimitiveTypes.Contains(t) && t != fromName)
                .Distinct();

            foreach (string dep in deps)
            {
                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, primary.Index), $"{fromName}->{dep}");
                string depKind = dep.EndsWith("Service", StringComparison.Ordinal) ? "service" : "dependency";
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("DEPENDS_ON"),
                    fromId, context.NodeId(Ng.Seg(depKind, dep)), new[] { ev }, Confidence.From(0.8)));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects Angular reactive-form fields — <c>fb.group({...})</c>/<c>new FormGroup({...})</c> field entries,
/// whether defined as <c>name: ['', Validators.required]</c> or <c>name: new FormControl('', [...])</c> —
/// and the built-in <c>Validators.*</c> calls attached to each, the Angular equivalent of React's
/// react-hook-form <c>register()</c> detection (see ReactFormFieldAnalyzer). Emits the same FormField node
/// kind/property shape (name/form/validation) so Documentation's existing "Forms" rendering picks it up
/// with no changes there.
/// </summary>
internal sealed class AngularFormFieldAnalyzer : IAnalyzer
{
    private static readonly Regex PrimaryClass = new(@"export\s+class\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex FormGroupStart =
        new(@"(?:new\s+FormGroup|\.group)\s*\(\s*\{", RegexOptions.Compiled);
    private static readonly Regex FieldStart =
        new(@"(\w+)\s*:\s*(\[|new\s+FormControl\s*\()", RegexOptions.Compiled);
    private static readonly Regex ValidatorRef =
        new(@"Validators\.(\w+)(?:\(([^()]*)\))?", RegexOptions.Compiled);

    public string Name => "angular-formfields";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            Match primary = PrimaryClass.Match(file.Text);
            string component = primary.Success ? primary.Groups[1].Value : Path.GetFileNameWithoutExtension(file.Path);

            foreach (Match start in FormGroupStart.Matches(file.Text))
            {
                int bodyStart = start.Index + start.Length - 1; // the '{'
                int bodyEnd = FindMatching(file.Text, bodyStart, '{', '}');
                if (bodyEnd < 0) continue;
                string body = file.Text[bodyStart..bodyEnd];

                foreach (Match m in FieldStart.Matches(body))
                {
                    string field = m.Groups[1].Value;
                    char openChar = m.Value[^1]; // the match always ends on the '[' or '(' delimiter itself
                    char closeChar = openChar == '[' ? ']' : ')';
                    int openIndex = m.Index + m.Length - 1;
                    int closeIndex = FindMatching(body, openIndex, openChar, closeChar);
                    if (closeIndex < 0) continue;
                    string def = body[openIndex..(closeIndex + 1)];

                    string validation = string.Join(", ", ValidatorRef.Matches(def)
                        .Select(v => v.Groups[2].Success && v.Groups[2].Value.Trim().Length > 0
                            ? $"{v.Groups[1].Value}({v.Groups[2].Value.Trim()})"
                            : v.Groups[1].Value)
                        .Distinct());

                    int absoluteIndex = bodyStart + m.Index;
                    var props = new List<(string, string)> { ("name", field), ("form", component) };
                    if (validation.Length > 0) props.Add(("validation", validation));
                    Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, absoluteIndex), field);
                    sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("formfield", $"{component}:{field}")),
                        NodeKind.From("FormField"), new[] { ev }, Confidence.From(0.8), Ng.Props(props.ToArray())));
                }
            }
        }

        return Task.CompletedTask;
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) { depth--; if (depth == 0) return i; }
        }

        return -1;
    }
}

/// <summary>
/// Detects Angular component filter state and classifies WHAT each field actually is, mirroring
/// ReactFilterAnalyzer's redesign (see that class for the full rationale) on the Angular side. Angular has
/// no <c>useState</c> equivalent — filter state lives as ordinary component class field declarations
/// instead (<c>filterContractId = '';</c>, <c>selectedStatusFilters: string[] = [];</c>). Classification is
/// purely structural, never a guess at business meaning: a <c>...Open</c> suffix is UI chrome (dropped
/// entirely), <c>selected...</c>/plural <c>...Filters</c> is multi-select, <c>...Tab</c> is tab-driven,
/// everything else is single-value. Usage is then traced — a <c>.filter()</c> predicate, a query-string
/// interpolation, an HttpParams-style builder call, or an object-literal key — using Angular's <c>this.</c>
/// member-access convention, to ground a <c>targetField</c> instead of only repeating the raw field name.
/// Scoped to files containing <c>@Component</c> so a same-named property on a plain data-model interface
/// (e.g. a request DTO) isn't mistaken for live UI filter state.
/// </summary>
internal sealed class AngularFilterAnalyzer : IAnalyzer
{
    private static readonly Regex PrimaryClass = new(@"export\s+class\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex FilterField = new(
        @"(?:public\s+|private\s+|protected\s+|readonly\s+)*(\w*[Ff]ilters?\w*)\s*(?::\s*[^=;\r\n]+)?\s*=\s*(?!=)",
        RegexOptions.Compiled);

    public string Name => "angular-filters";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!file.Text.Contains("@Component")) continue;

            Match primary = PrimaryClass.Match(file.Text);
            string component = primary.Success ? primary.Groups[1].Value : Path.GetFileNameWithoutExtension(file.Path);

            foreach (Match m in FilterField.Matches(file.Text).DistinctBy(m => m.Groups[1].Value))
            {
                string field = m.Groups[1].Value;
                string? kind = ClassifyShape(field);
                if (kind is null) continue; // "...Open" toggle-shaped UI chrome, not a filter criterion

                var props = new List<(string, string)> { ("name", field), ("component", component), ("kind", kind) };
                if (FindTargetField(file.Text, field) is { Length: > 0 } target) props.Add(("targetField", target));

                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), field);
                sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("filter", $"{component}:{field}")),
                    NodeKind.From("Filter"), new[] { ev }, Confidence.From(0.75), Ng.Props(props.ToArray())));
            }
        }

        return Task.CompletedTask;
    }

    internal static string? ClassifyShape(string name)
    {
        if (name.EndsWith("Open", StringComparison.Ordinal)) return null;
        if (name.StartsWith("selected", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Filters", StringComparison.Ordinal)) return "multi-select";
        if (name.EndsWith("Tab", StringComparison.Ordinal)) return "tab";
        return "single-value";
    }

    // Same disclosed limitation as ReactFilterAnalyzer's equivalent helper: a file-wide regex search rather
    // than a scoped AST walk, so a file with multiple unrelated .filter() calls could in principle attribute
    // the wrong field.
    internal static string? FindTargetField(string text, string name)
    {
        string n = Regex.Escape(name);

        Match m = Regex.Match(text, @"\.filter\s*\(\s*\w+\s*=>[\s\S]{0,120}?\.(\w+)\s*(?:===?|!==?)\s*this\." + n + @"\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"\.filter\s*\(\s*\w+\s*=>[\s\S]{0,120}?this\." + n + @"\s*(?:===?|!==?)\s*\w+\.(\w+)\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"[?&]([\w-]+)=\$\{this\." + n + @"\}");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"\.(?:append|set)\s*\(\s*['""`]([\w-]+)['""`]\s*,\s*this\." + n + @"\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(text, @"(\w+)\s*:\s*this\." + n + @"\s*[,}]");
        if (m.Success) return m.Groups[1].Value;

        return null;
    }
}

/// <summary>
/// Detects HttpClient calls (this.http.get/post/…) and the backend URLs they target. Resolves the common
/// Angular pattern where the URL is built from a base field — <c>private apiUrl = '…/api/tasks'</c> then
/// <c>http.get(this.apiUrl)</c> or <c>http.get(`${this.apiUrl}/${id}`)</c> — by substituting the field's
/// value and stripping the host, so calls resolve to real backend routes (e.g. <c>/api/tasks/{}</c>).
/// </summary>
internal sealed class HttpClientAnalyzer : IAnalyzer
{
    // First argument may be a string/template literal OR a variable expression (this.apiUrl).
    private static readonly Regex HttpCall =
        new(@"\.http\s*\.\s*(get|post|put|delete|patch)\s*(?:<[^>]*>)?\s*\(\s*([^,)]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // String/const fields that hold a URL base: `apiUrl = 'http://…/api/tasks'`, `baseUrl: "…"`, etc.
    private static readonly Regex UrlField =
        new(@"(\w+)\s*[:=]\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled);

    private static readonly Regex Host = new(@"^https?://[^/]+", RegexOptions.Compiled);

    public string Name => "angular-http";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            // Per-file map of base-URL fields to substitute into calls.
            var bases = new Dictionary<string, string>();
            foreach (Match f in UrlField.Matches(file.Text))
            {
                string val = f.Groups[2].Value;
                if (val.Contains("/api") || val.StartsWith("http") || val.StartsWith('/'))
                    bases[f.Groups[1].Value] = val;
            }

            foreach (Match m in HttpCall.Matches(file.Text))
            {
                string verb = m.Groups[1].Value.ToUpperInvariant();
                string? url = ResolveUrl(m.Groups[2].Value, bases);
                if (url is null) continue;
                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), $"{verb} {url}");
                sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("apicall", $"{verb} {url}")),
                    NodeKind.From("ApiCall"), new[] { ev }, Confidence.From(0.8),
                    Ng.Props(("verb", verb), ("url", url))));
            }
        }

        return Task.CompletedTask;
    }

    private static string? ResolveUrl(string rawArg, IReadOnlyDictionary<string, string> bases)
    {
        string s = rawArg.Trim().Trim('`', '\'', '"').Trim();

        // Whole-argument variable reference: http.get(this.apiUrl).
        string whole = s.StartsWith("this.") ? s[5..] : s;
        if (bases.TryGetValue(whole, out string? full)) s = full;
        else
            foreach ((string field, string value) in bases)                 // interpolated: `${this.apiUrl}/${id}`
                s = s.Replace("${this." + field + "}", value).Replace("${" + field + "}", value);

        s = Host.Replace(s, "");                                            // strip protocol + host
        if (!s.StartsWith('/')) s = "/" + s;
        // Keep only calls we could ground to a real API path; drop unresolved bare variables.

        return s.Contains("/api", StringComparison.OrdinalIgnoreCase) ? s : null;
    }
}
