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
                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), className);
                sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("component", className)),
                    NodeKind.From("UIComponent"), new[] { ev }, Confidence.From(0.9),
                    Ng.Props(("name", className), ("selector", selector))));
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

/// <summary>Detects Angular routes (path entries in route configurations).</summary>
internal sealed class AngularRouteAnalyzer : IAnalyzer
{
    private static readonly Regex Route =
        new(@"path\s*:\s*['""`]([^'""`]*)['""`]", RegexOptions.Compiled);

    public string Name => "angular-routes";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;
        foreach (TsFile file in model.Files)
        {
            if (!file.Path.Contains("routing", StringComparison.OrdinalIgnoreCase) &&
                !file.Text.Contains("Routes", StringComparison.Ordinal))
                continue;

            foreach (Match m in Route.Matches(file.Text))
            {
                string path = m.Groups[1].Value;
                string display = string.IsNullOrEmpty(path) ? "(default)" : path;
                Evidence ev = context.Evidence(file.Path, Ng.LineAt(file.Text, m.Index), $"path:{display}");
                sink.Add(NodeDiscovery.Create(context.NodeId(Ng.Seg("route", display)),
                    NodeKind.From("Route"), new[] { ev }, Confidence.From(0.85), Ng.Props(("path", display))));
            }
        }

        return Task.CompletedTask;
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

/// <summary>Detects Angular route guards and HTTP interceptors (class-based and functional).</summary>
internal sealed class AngularGuardAnalyzer : IAnalyzer
{
    private static readonly Regex ClassImpl =
        new(@"export\s+class\s+(\w+)\s+implements\s+([^{]+)\{", RegexOptions.Compiled);
    private static readonly Regex Functional =
        new(@"export\s+const\s+(\w+)\s*:\s*(CanActivate(?:Child)?Fn|CanMatchFn|CanDeactivateFn|HttpInterceptorFn)", RegexOptions.Compiled);

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
                    (ifaces.Contains("CanActivate") || ifaces.Contains("CanDeactivate") || ifaces.Contains("CanMatch")) ? "Guard" : null;
                if (kind is not null) Emit(context, sink, file, m, m.Groups[1].Value, kind);
            }
            foreach (Match m in Functional.Matches(file.Text))
                Emit(context, sink, file, m, m.Groups[1].Value, m.Groups[2].Value.Contains("Interceptor") ? "Interceptor" : "Guard");
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
