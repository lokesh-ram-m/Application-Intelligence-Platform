using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aip.Plugins.AspNetCore;

/// <summary>Semantic helpers over the Roslyn model — symbols, canonical names, inheritance, attributes.</summary>
internal static class Sym
{
    private static readonly SymbolDisplayFormat Canonical = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    /// <summary>Deterministic canonical name: namespace + containing types + name + generic arity.</summary>
    public static string Name(ITypeSymbol symbol) => symbol.ToDisplayString(Canonical);

    public static IEnumerable<(TypeDeclarationSyntax Decl, INamedTypeSymbol Symbol, SemanticModel Model, string Path)> Types(RoslynSemanticModel model)
    {
        foreach (SyntaxTree tree in model.Trees)
        {
            SemanticModel sm = model.GetSemanticModel(tree);
            string path = model.PathOf(tree);
            foreach (TypeDeclarationSyntax decl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                if (sm.GetDeclaredSymbol(decl) is INamedTypeSymbol s)
                    yield return (decl, s, sm, path);
        }
    }

    public static IEnumerable<INamedTypeSymbol> BaseChain(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol? b = symbol.BaseType; b is not null; b = b.BaseType) yield return b;
    }

    /// <summary>Semantic inheritance check with a syntactic fallback for unreferenced framework base types.</summary>
    public static bool InheritsFrom(INamedTypeSymbol symbol, TypeDeclarationSyntax decl, string baseName) =>
        BaseChain(symbol).Any(b => b.Name == baseName) ||
        (decl.BaseList?.Types.Any(t => t.Type.ToString().Split('.').Last().StartsWith(baseName)) ?? false);

    public static bool HasAttribute(ISymbol symbol, TypeDeclarationSyntax? decl, string name) =>
        symbol.GetAttributes().Any(a => Matches(a.AttributeClass?.Name, name)) ||
        (decl?.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Contains(name)) ?? false);

    private static bool Matches(string? attr, string name) =>
        attr is not null && (attr == name || attr == name + "Attribute");

    public static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // The project that declares a symbol — its nearest .csproj — so cross-project relationships target the
    // node the owning project actually emitted (identities are project-scoped).
    private static readonly ConcurrentDictionary<string, string?> ProjectByDir = new(StringComparer.OrdinalIgnoreCase);

    public static string? ProjectOf(ISymbol symbol)
    {
        Location? loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        string? file = loc?.SourceTree?.FilePath;
        if (string.IsNullOrEmpty(file)) return null;

        return ProjectByDir.GetOrAdd(Path.GetDirectoryName(file) ?? "", dir =>
        {
            var d = new DirectoryInfo(dir);
            while (d is not null)
            {
                FileInfo? csproj = d.GetFiles("*.csproj").FirstOrDefault();
                if (csproj is not null) return Path.GetFileNameWithoutExtension(csproj.Name);
                d = d.Parent;
            }

            return null;
        });
    }

    public static IdentitySegment Seg(string kind, string value) => IdentitySegment.Seg(kind, value);

    public static Dictionary<string, string> Props(params (string Key, string Value)[] pairs) => PropertyBag.Props(pairs);

    // Project-scoped identity for a resolved type symbol — shared by every analyzer that targets another
    // in-source type (entities, DI implementations, fluent-API relationship endpoints, …).
    public static KnowledgeIdentity TypeId(IAnalysisContext context, string fallbackProject, ITypeSymbol t) =>
        context.NodeId(Seg("project", ProjectOf(t) ?? fallbackProject), Seg("type", Name(t)));

    // Unwrap List<T>/ICollection<T>/… to T; returns null for non-collections.
    public static ITypeSymbol? ElementOfCollection(ITypeSymbol t) =>
        t is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } nt
        && nt.Name is "List" or "IList" or "ICollection" or "IEnumerable" or "HashSet" or "Collection" or "IReadOnlyList" or "IReadOnlyCollection"
            ? nt.TypeArguments[0] : null;
}

/// <summary>
/// Detects controllers and their HTTP endpoints, resolving final routes the way ASP.NET Core does:
/// <c>[controller]</c>/<c>[action]</c> tokens are substituted, controller- and action-level templates
/// are combined, absolute action routes override, and conventional MVC actions (no template) fall back
/// to <c>/{controller}/{action}</c>. Each action becomes a distinct endpoint (identity carries the
/// resolved route), so actions no longer collapse onto one placeholder route.
/// </summary>
internal sealed class ControllerAnalyzer : IAnalyzer
{
    public string Name => "controllers";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            // A real base type or [ApiController] is a structural, unambiguous signal; a bare name suffix
            // with neither is just a naming convention — a utility class named "AccessController" would
            // otherwise get the exact same confidence as a genuine ASP.NET Core controller.
            bool structural = Sym.InheritsFrom(symbol, decl, "ControllerBase")
                || Sym.InheritsFrom(symbol, decl, "Controller")
                || Sym.HasAttribute(symbol, decl, "ApiController");
            bool isController = structural || symbol.Name.EndsWith("Controller", StringComparison.Ordinal);
            if (!isController) continue;

            string fq = Sym.Name(symbol);
            SemanticModel sem = model.GetSemanticModel(decl.SyntaxTree);
            // Authorization and the [Route] template may be declared on a base controller — inherit both.
            string? controllerAuth = Authorization(decl.AttributeLists, sem) ?? InheritedAuthorization(symbol);
            KnowledgeIdentity controllerId = context.NodeId(Sym.Seg("project", project), Sym.Seg("type", fq));
            var cprops = new List<(string, string)> { ("name", symbol.Name) };
            if (controllerAuth is not null) cprops.Add(("authorize", controllerAuth));
            Confidence controllerConfidence = structural ? Confidence.Full : new Confidence(0.6);
            sink.Add(NodeDiscovery.Create(controllerId, NodeKind.From("Controller"),
                new[] { context.Evidence(path, Sym.Line(decl), fq) }, controllerConfidence, Sym.Props(cprops.ToArray())));

            string? controllerTemplate = RouteTemplate(decl.AttributeLists) ?? InheritedRoute(symbol);
            foreach (MethodDeclarationSyntax method in decl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;
                (string? verb, string? actionTemplate) = HttpVerb(method);
                if (verb is null) continue;
                actionTemplate ??= RouteTemplate(method.AttributeLists);

                string route = ResolveRoute(symbol.Name, controllerTemplate, actionTemplate, method.Identifier.Text);
                string? epAuth = Authorization(method.AttributeLists, sem) ?? controllerAuth;
                KnowledgeIdentity endpointId = context.AppNodeId(Sym.Seg("endpoint", $"{verb} {route}"));
                Evidence ev = context.Evidence(path, Sym.Line(method), method.Identifier.Text);
                var eprops = new List<(string, string)> { ("verb", verb), ("route", route), ("action", method.Identifier.Text) };
                if (epAuth is not null) eprops.Add(("authorize", epAuth));
                sink.Add(NodeDiscovery.Create(endpointId, NodeKind.From("Endpoint"), new[] { ev }, Confidence.Full,
                    Sym.Props(eprops.ToArray())));
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("EXPOSES"), controllerId, endpointId, new[] { ev }, Confidence.Full));
            }
        }

        return Task.CompletedTask;
    }

    // Combine controller + action templates into a final path, substituting [controller]/[action].
    private static string ResolveRoute(string controllerName, string? controllerTemplate, string? actionTemplate, string methodName)
    {
        string shortName = controllerName.EndsWith("Controller", StringComparison.Ordinal)
            ? controllerName[..^"Controller".Length] : controllerName;

        string ct = Substitute(controllerTemplate, shortName, methodName);
        string at = Substitute(actionTemplate, shortName, methodName);

        string combined =
            at.StartsWith('/') || at.StartsWith("~/") ? at.TrimStart('~') :
            at.Length == 0 ? ct :
            ct.Length == 0 ? at :
            ct.TrimEnd('/') + "/" + at.TrimStart('/');

        if (combined.Trim('/').Length == 0) combined = $"{shortName}/{methodName}"; // conventional MVC

        return Normalize(combined);
    }

    private static string Substitute(string? template, string controller, string action) =>
        (template ?? "")
            .Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        while (path.Contains("//")) path = path.Replace("//", "/");

        return "/" + path.Trim('/');
    }

    private static string? RouteTemplate(SyntaxList<AttributeListSyntax> lists)
    {
        AttributeSyntax? route = lists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString().Split('.').Last().StartsWith("Route"));

        return route is not null ? StringArg(route) : null;
    }

    // Many APIs put [Route("api/[controller]")] on a shared base controller; resolve it up the base chain
    // so a derived controller's endpoints keep their prefix instead of collapsing to "/{id}".
    private static string? InheritedRoute(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol? b = symbol.BaseType; b is not null; b = b.BaseType)
        {
            AttributeData? route = b.GetAttributes().FirstOrDefault(a => (a.AttributeClass?.Name ?? "").StartsWith("Route"));
            if (route?.ConstructorArguments is [{ Value: string t }, ..]) return t;
        }

        return null;
    }

    private static string? InheritedAuthorization(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol? b = symbol.BaseType; b is not null; b = b.BaseType)
        {
            var attrs = b.GetAttributes();
            if (attrs.Any(a => a.AttributeClass?.Name is "AllowAnonymousAttribute")) return "AllowAnonymous";
            AttributeData? auth = attrs.FirstOrDefault(a => a.AttributeClass?.Name is "AuthorizeAttribute");
            if (auth is null) continue;
            var roles = auth.NamedArguments.FirstOrDefault(n => n.Key == "Roles").Value.Value as string;

            return roles is not null ? $"Authorize (Roles: {roles})" : "Authorize";
        }

        return null;
    }

    // Reads [Authorize]/[AllowAnonymous] into a short label: "AllowAnonymous", "Authorize", or "Authorize (Roles: …)".
    // A Policy/Roles argument is very often a named constant (e.g. [Authorize(Policy = Roles.PolicyAdmin)])
    // rather than a string literal — the semantic model's constant-value resolution handles that identically
    // to a literal, so the real policy/role name is captured either way instead of silently dropping the
    // property whenever the argument isn't written as an inline string.
    private static string? Authorization(SyntaxList<AttributeListSyntax> lists, SemanticModel semanticModel)
    {
        var attrs = lists.SelectMany(a => a.Attributes).ToList();
        if (attrs.Any(a => a.Name.ToString().Split('.').Last() == "AllowAnonymous")) return "AllowAnonymous";

        AttributeSyntax? auth = attrs.FirstOrDefault(a => a.Name.ToString().Split('.').Last() == "Authorize");
        if (auth is null) return null;

        var details = new List<string>();
        foreach (AttributeArgumentSyntax arg in auth.ArgumentList?.Arguments ?? default)
        {
            Optional<object?> constant = semanticModel.GetConstantValue(arg.Expression);
            if (constant is not { HasValue: true, Value: string val }) continue;
            string? name = arg.NameEquals?.Name.Identifier.Text;
            details.Add(name == "Roles" ? $"Roles: {val}" : $"Policy: {val}");
        }

        return details.Count > 0 ? $"Authorize ({string.Join(", ", details)})" : "Authorize";
    }

    private static (string? Verb, string? Template) HttpVerb(MethodDeclarationSyntax method)
    {
        foreach (AttributeSyntax attr in method.AttributeLists.SelectMany(a => a.Attributes))
        {
            string n = attr.Name.ToString().Split('.').Last();
            if (n.StartsWith("Http", StringComparison.Ordinal) && n.Length > 4)
                return (n["Http".Length..].Replace("Attribute", "").ToUpperInvariant(), StringArg(attr));
        }

        return (null, null);
    }

    private static string? StringArg(AttributeSyntax a) =>
        (a.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
}

/// <summary>
/// Detects Azure Functions — a serverless entry point analogous to an HTTP <c>Endpoint</c>, but triggered
/// by a queue message, timer, or raw HTTP call rather than routed ASP.NET Core request handling.
/// <c>[Function("Name")]</c> (isolated-worker) or <c>[FunctionName("Name")]</c> (in-process) identifies the
/// function; a trigger attribute (<c>[QueueTrigger]</c>/<c>[TimerTrigger]</c>/<c>[HttpTrigger]</c>/…) on one
/// of its parameters identifies what invokes it — both hosting models use the same trigger attribute names,
/// so one scan covers both. Without this, an entire serverless processing tier is invisible: it lives
/// outside the MVC controller/endpoint pipeline every other analyzer here assumes.
/// </summary>
internal sealed class AzureFunctionAnalyzer : IAnalyzer
{
    public string Name => "azure-functions";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            foreach (MethodDeclarationSyntax method in decl.Members.OfType<MethodDeclarationSyntax>())
            {
                string? functionName = FunctionName(method.AttributeLists);
                if (functionName is null) continue;

                string? trigger = TriggerDescription(method);
                KnowledgeIdentity functionId = context.AppNodeId(Sym.Seg("function", functionName));
                var props = new List<(string, string)> { ("name", functionName) };
                if (trigger is not null) props.Add(("trigger", trigger));
                Evidence ev = context.Evidence(path, Sym.Line(method), functionName);
                sink.Add(NodeDiscovery.Create(functionId, NodeKind.From("AzureFunction"),
                    new[] { ev }, Confidence.Full, Sym.Props(props.ToArray())));
            }
        }

        return Task.CompletedTask;
    }

    private static string? FunctionName(SyntaxList<AttributeListSyntax> lists)
    {
        AttributeSyntax? attr = lists.SelectMany(a => a.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Split('.').Last() is "Function" or "FunctionName");

        return attr is null ? null : StringArg(attr);
    }

    private static string? TriggerDescription(MethodDeclarationSyntax method)
    {
        foreach (ParameterSyntax param in method.ParameterList.Parameters)
        {
            foreach (AttributeSyntax attr in param.AttributeLists.SelectMany(a => a.Attributes))
            {
                string n = attr.Name.ToString().Split('.').Last();
                if (!n.EndsWith("Trigger", StringComparison.Ordinal)) continue;
                string? detail = StringArg(attr);

                return detail is null ? n : $"{n}:{detail}";
            }
        }

        return null;
    }

    private static string? StringArg(AttributeSyntax a) =>
        (a.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
}

/// <summary>
/// Detects services — either by name suffix (*Service) or, more reliably, by implementing an interface
/// whose own name ends in "Service" (e.g. <c>OrderProcessor : IOrderService</c>), which naming-only
/// detection misses entirely despite it being a very common pattern once a project introduces interfaces
/// for DI.
/// </summary>
internal sealed class ServiceAnalyzer : IAnalyzer
{
    public string Name => "services";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;
        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            bool contractMatch = symbol.AllInterfaces.Any(i => i.Name.EndsWith("Service", StringComparison.Ordinal));
            bool nameMatch = symbol.Name.EndsWith("Service", StringComparison.Ordinal);
            if (!contractMatch && !nameMatch) continue;

            string fq = Sym.Name(symbol);
            Confidence confidence = contractMatch ? new Confidence(0.85) : new Confidence(0.6);
            sink.Add(NodeDiscovery.Create(context.NodeId(Sym.Seg("project", project), Sym.Seg("type", fq)),
                NodeKind.From("Service"), new[] { context.Evidence(path, Sym.Line(decl), fq) }, confidence, Sym.Props(("name", symbol.Name))));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects repositories — either by name suffix (*Repository) or, more reliably, by implementing an
/// interface whose own name ends in "Repository" (e.g. <c>SqlOrderStore : IOrderRepository</c>), which
/// naming-only detection misses entirely.
/// </summary>
internal sealed class RepositoryAnalyzer : IAnalyzer
{
    public string Name => "repositories";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;
        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            bool contractMatch = symbol.AllInterfaces.Any(i => i.Name.EndsWith("Repository", StringComparison.Ordinal));
            bool nameMatch = symbol.Name.EndsWith("Repository", StringComparison.Ordinal);
            if (!contractMatch && !nameMatch) continue;

            string fq = Sym.Name(symbol);
            Confidence confidence = contractMatch ? new Confidence(0.85) : new Confidence(0.6);
            sink.Add(NodeDiscovery.Create(context.NodeId(Sym.Seg("project", project), Sym.Seg("type", fq)),
                NodeKind.From("Repository"), new[] { context.Evidence(path, Sym.Line(decl), fq) }, confidence, Sym.Props(("name", symbol.Name))));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Detects interfaces and, semantically, the classes that implement or extend in-source types.</summary>
internal sealed class InterfaceAnalyzer : IAnalyzer
{
    public string Name => "interfaces";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            // Scope each type to the project that declares it, so cross-project IMPLEMENTS/EXTENDS connect.
            KnowledgeIdentity Id(ITypeSymbol s) => context.NodeId(Sym.Seg("project", Sym.ProjectOf(s) ?? project), Sym.Seg("type", Sym.Name(s)));

            if (symbol.TypeKind == TypeKind.Interface)
            {
                string fq = Sym.Name(symbol);
                sink.Add(NodeDiscovery.Create(Id(symbol), NodeKind.From("Interface"),
                    new[] { context.Evidence(path, Sym.Line(decl), fq) }, Confidence.Full, Sym.Props(("name", symbol.Name))));
                continue;
            }

            if (symbol.TypeKind != TypeKind.Class) continue;
            Evidence ev = context.Evidence(path, Sym.Line(decl), symbol.Name);

            // IMPLEMENTS — semantic, only for in-source interfaces (skip framework interfaces).
            foreach (INamedTypeSymbol iface in symbol.Interfaces.Where(i => i.Locations.Any(l => l.IsInSource)))
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("IMPLEMENTS"), Id(symbol), Id(iface), new[] { ev }, Confidence.Full));

            // EXTENDS — semantic, only for in-source base classes.
            if (symbol.BaseType is { SpecialType: SpecialType.None } b && b.Locations.Any(l => l.IsInSource))
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("EXTENDS"), Id(symbol), Id(b), new[] { ev }, Confidence.Full));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects audit/lifecycle logging call sites — e.g. <c>_audit.LogAsync("Contract", id, "Created", ...)</c>
/// — a strong signal that an entity type has a tracked lifecycle (who did what, when to it), invisible to
/// every other analyzer here since it's just an ordinary method call, not a framework registration or
/// attribute. Recognized by shape, not any specific audit library: the receiver's own text contains "audit"
/// and the invoked method's name contains "Log", with a string-literal first argument naming the audited
/// entity type. Deduped per (class, entity type) — this grounds "this class audits entity X", not each
/// individual action name, since <see cref="Relationship"/> has no property bag to carry that granularity.
/// Resolved to the actual Entity node it names later by AuditLogToEntityResolver (see
/// Aip.Knowledge/RelationshipResolution.cs) — this analyzer only grounds the raw fact.
/// </summary>
internal sealed class AuditLogAnalyzer : IAnalyzer
{
    public string Name => "audit-log";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            var seen = new HashSet<string>();
            foreach (InvocationExpressionSyntax inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax member) continue;
                if (!member.Name.Identifier.Text.Contains("Log", StringComparison.Ordinal)) continue;
                if (!member.Expression.ToString().Contains("audit", StringComparison.OrdinalIgnoreCase)) continue;
                string? entityType = FirstStringLiteralArg(inv);
                if (entityType is null || !seen.Add(entityType)) continue;

                Evidence ev = context.Evidence(path, Sym.Line(inv), $"{symbol.Name}:{entityType}");
                sink.Add(NodeDiscovery.Create(
                    context.NodeId(Sym.Seg("project", project), Sym.Seg("auditlog", $"{symbol.Name}:{entityType}")),
                    NodeKind.From("AuditLog"), new[] { ev }, Confidence.From(0.7),
                    Sym.Props(("entityType", entityType), ("source", symbol.Name))));
            }
        }

        return Task.CompletedTask;
    }

    private static string? FirstStringLiteralArg(InvocationExpressionSyntax inv) =>
        inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
            .Select(l => l.Token.Value as string).FirstOrDefault(s => s is not null);
}

/// <summary>
/// Detects a status/lifecycle "workflow" concept from repeated comparisons against the same *Status/*State
/// -named member across a class — e.g. <c>EmploymentStatus == "Active"</c>, then <c>== "Inactive"</c>
/// elsewhere in the same class. Scoped deliberately narrow: only a member whose name ends in Status/State
/// AND is compared against at least two DISTINCT string literals within the same class counts — a single
/// <c>==</c> check reads as an ordinary guard clause, not a workflow with real, enumerable states. Any
/// looser than that risks flagging routine conditionals as a "business workflow" that isn't really one.
/// </summary>
internal sealed class StatusWorkflowAnalyzer : IAnalyzer
{
    private static readonly Regex StatusComparison =
        new(@"\b(\w*(?:Status|State))\s*==\s*[""'](\w[\w\s-]*)[""']", RegexOptions.Compiled);

    public string Name => "status-workflow";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;

            var byProperty = StatusComparison.Matches(decl.ToString())
                .GroupBy(m => m.Groups[1].Value)
                .Where(g => g.Select(m => m.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2);

            foreach (IGrouping<string, Match> group in byProperty)
            {
                string property = group.Key;
                var values = group.Select(m => m.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
                Evidence ev = context.Evidence(path, Sym.Line(decl), $"{symbol.Name}.{property}");
                sink.Add(NodeDiscovery.Create(
                    context.NodeId(Sym.Seg("project", project), Sym.Seg("workflow", $"{symbol.Name}:{property}")),
                    NodeKind.From("StatusWorkflow"), new[] { ev }, Confidence.From(0.6),
                    Sym.Props(("name", property), ("owner", symbol.Name), ("values", string.Join(", ", values)))));
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects manual business-rule/invariant validation — a hand-rolled <c>if (...) throw new
/// InvalidOperationException("...")</c> guard, structurally identical in intent to what FluentValidation
/// linkage already captures for validator classes, but never scanned for in plain Service/Manager methods.
/// Scoped deliberately narrow (see Wave C risk note in the plan): only inside a method already named
/// Validate*/Check*/Ensure*, inside a class already classified Service/Manager by naming convention — any
/// looser than that would flag routine defensive null-checks as "business rules."
/// </summary>
internal sealed class BusinessRuleAnalyzer : IAnalyzer
{
    private static readonly HashSet<string> RuleExceptionTypes = new(StringComparer.Ordinal)
        { "InvalidOperationException", "ValidationException", "ArgumentException" };

    public string Name => "business-rules";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            bool isServiceOrManager = symbol.Name.EndsWith("Service", StringComparison.Ordinal) || symbol.Name.EndsWith("Manager", StringComparison.Ordinal);
            if (!isServiceOrManager) continue;

            foreach (MethodDeclarationSyntax method in decl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!IsValidationShapedName(method.Identifier.Text)) continue;

                foreach (ThrowStatementSyntax throwStmt in method.DescendantNodes().OfType<ThrowStatementSyntax>())
                {
                    if (throwStmt.Expression is not ObjectCreationExpressionSyntax creation) continue;
                    string exceptionType = creation.Type.ToString().Split('.').Last();
                    if (!RuleExceptionTypes.Contains(exceptionType)) continue;
                    string? message = creation.ArgumentList?.Arguments.Select(a => a.Expression)
                        .OfType<LiteralExpressionSyntax>().FirstOrDefault()?.Token.Value as string;
                    if (message is null) continue;

                    int line = Sym.Line(throwStmt);
                    Evidence ev = context.Evidence(path, line, method.Identifier.Text);
                    sink.Add(NodeDiscovery.Create(
                        context.NodeId(Sym.Seg("project", project), Sym.Seg("businessrule", $"{symbol.Name}.{method.Identifier.Text}:{line}")),
                        NodeKind.From("BusinessRule"), new[] { ev }, Confidence.From(0.6),
                        Sym.Props(("rule", message), ("owner", symbol.Name), ("method", method.Identifier.Text))));
                }
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsValidationShapedName(string name) =>
        name.StartsWith("Validate", StringComparison.Ordinal) || name.StartsWith("Check", StringComparison.Ordinal) || name.StartsWith("Ensure", StringComparison.Ordinal);
}

/// <summary>Detects entities semantically: data classes (properties only, no ordinary methods).</summary>
internal sealed class EntityAnalyzer : IAnalyzer
{
    public string Name => "entities";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;
            if (symbol.Name.EndsWith("Service", StringComparison.Ordinal) || symbol.Name.EndsWith("Controller", StringComparison.Ordinal)) continue;
            if (IsDto(symbol.Name)) continue;   // DTOs / requests / commands are not domain entities
            if (Sym.BaseChain(symbol).Any(b => b.Name.Contains("DbContext"))) continue;

            var properties = symbol.GetMembers().OfType<IPropertySymbol>().Where(p => p.DeclaredAccessibility == Accessibility.Public).ToList();
            bool hasOrdinaryMethods = symbol.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary);
            if (properties.Count == 0 || hasOrdinaryMethods) continue;

            string fq = Sym.Name(symbol);
            Evidence ev = context.Evidence(path, Sym.Line(decl), fq);
            KnowledgeIdentity entityId = context.NodeId(Sym.Seg("project", Sym.ProjectOf(symbol) ?? project), Sym.Seg("type", fq));

            // Split properties into scalar fields (a summary) and navigation properties (graph relationships);
            // note which fields carry DataAnnotations validation attributes.
            var fields = new List<string>();
            var validated = new List<string>();
            foreach (IPropertySymbol p in properties)
            {
                if (p.GetAttributes().Any(IsValidationAttr)) validated.Add(p.Name);

                ITypeSymbol? element = Sym.ElementOfCollection(p.Type);
                if (element is not null && IsEntityLike(element, symbol))
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("HAS_MANY"), entityId, Sym.TypeId(context, project, element), new[] { ev }, Confidence.From(0.85)));
                else if (IsEntityLike(p.Type, symbol))
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("REFERENCES"), entityId, Sym.TypeId(context, project, p.Type), new[] { ev }, Confidence.From(0.85)));
                else
                    fields.Add($"{p.Name}: {TypeName(p.Type)}");
            }

            string? key = properties.FirstOrDefault(p => p.Name == "Id" || p.GetAttributes().Any(a => a.AttributeClass?.Name == "KeyAttribute"))?.Name;
            var eprops = new List<(string, string)> { ("name", symbol.Name) };
            if (fields.Count > 0) eprops.Add(("fields", string.Join("; ", fields.Take(60))));
            if (validated.Count > 0) eprops.Add(("validated", string.Join(", ", validated.Take(40))));
            if (key is not null) eprops.Add(("key", key));

            sink.Add(NodeDiscovery.Create(entityId, NodeKind.From("Entity"), new[] { ev }, Confidence.From(0.9), Sym.Props(eprops.ToArray())));
        }

        return Task.CompletedTask;
    }

    private static readonly string[] DtoSuffixes = { "Request", "Response", "Dto", "DTO", "ViewModel", "Vm", "Command", "Query", "Payload" };
    internal static bool IsDto(string name) => DtoSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));

    // DataAnnotations validation attributes (the attribute class name without the "Attribute" suffix).
    private static readonly HashSet<string> ValidationAttrNames = new(StringComparer.Ordinal)
        { "Required", "MaxLength", "MinLength", "StringLength", "Range", "EmailAddress", "Phone", "Url", "RegularExpression", "Compare", "CreditCard" };
    private static bool IsValidationAttr(AttributeData a) =>
        ValidationAttrNames.Contains((a.AttributeClass?.Name ?? "").Replace("Attribute", ""));

    // An in-source domain class (another entity), not a framework type, DTO, service/controller/repo.
    private static bool IsEntityLike(ITypeSymbol t, INamedTypeSymbol self) =>
        t is INamedTypeSymbol { TypeKind: TypeKind.Class, SpecialType: SpecialType.None } nt
        && nt.Locations.Any(l => l.IsInSource)
        && !SymbolEqualityComparer.Default.Equals(nt, self)
        && !IsDto(nt.Name)
        && !nt.Name.EndsWith("Service", StringComparison.Ordinal)
        && !nt.Name.EndsWith("Controller", StringComparison.Ordinal)
        && !nt.Name.EndsWith("Repository", StringComparison.Ordinal)
        && !nt.Name.EndsWith("DbContext", StringComparison.Ordinal);

    private static string TypeName(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
}

/// <summary>
/// Detects EF Core DbContexts and DbSet&lt;T&gt; entities, resolving T semantically. Also extracts entity
/// relationships configured via the Fluent API — <c>OnModelCreating</c>'s
/// <c>modelBuilder.Entity&lt;T&gt;().HasOne(...).WithMany(...)</c> chains, and the equivalent
/// <c>IEntityTypeConfiguration&lt;T&gt;.Configure(builder)</c> pattern — which navigation-property-only
/// inspection (see <see cref="EntityAnalyzer"/>) can't see. This also recovers entities that
/// <see cref="EntityAnalyzer"/> deliberately skips because they carry real behavior methods (a rich domain
/// model, not a plain data class) but are still unambiguously registered as EF Core entities here.
/// </summary>
internal sealed class DbContextAnalyzer : IAnalyzer
{
    public string Name => "dbcontext";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, SemanticModel sm, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;

            if (Sym.InheritsFrom(symbol, decl, "DbContext"))
            {
                KnowledgeIdentity storeId = context.NodeId(Sym.Seg("project", project), Sym.Seg("datastore", symbol.Name));
                Evidence ev = context.Evidence(path, Sym.Line(decl), symbol.Name);
                sink.Add(NodeDiscovery.Create(storeId, NodeKind.From("DataStore"), new[] { ev }, Confidence.Full,
                    Sym.Props(("name", symbol.Name), ("kind", "ef-dbcontext"))));

                foreach (PropertyDeclarationSyntax prop in decl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if (prop.Type is not GenericNameSyntax g || g.Identifier.Text != "DbSet") continue;
                    TypeSyntax arg = g.TypeArgumentList.Arguments[0];
                    ITypeSymbol? ets = sm.GetSymbolInfo(arg).Symbol as ITypeSymbol;
                    string entityName = ets is not null ? Sym.Name(ets) : arg.ToString();
                    string entityProject = ets is not null ? (Sym.ProjectOf(ets) ?? project) : project;
                    KnowledgeIdentity entityId = context.NodeId(Sym.Seg("project", entityProject), Sym.Seg("type", entityName));
                    Evidence pev = context.Evidence(path, Sym.Line(prop), prop.Identifier.Text);
                    sink.Add(NodeDiscovery.Create(entityId, NodeKind.From("Entity"), new[] { pev }, Confidence.Full, Sym.Props(("name", entityName.Split('.').Last()))));
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("OWNS"), storeId, entityId, new[] { pev }, Confidence.Full));
                }

                MethodDeclarationSyntax? onModelCreating = decl.Members.OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating");
                if (onModelCreating is not null)
                    ExtractFluentRelationships(context, sink, sm, path, onModelCreating, project,
                        expr => EntityCallRoot(sm, expr));

                continue;
            }

            // IEntityTypeConfiguration<T>.Configure(EntityTypeBuilder<T> builder) — the file-per-entity
            // alternative to inline OnModelCreating configuration, at least as common in larger codebases.
            INamedTypeSymbol? config = symbol.AllInterfaces.FirstOrDefault(i => i.Name == "IEntityTypeConfiguration");
            if (config is null || config.TypeArguments.FirstOrDefault() is not ITypeSymbol configuredType) continue;
            MethodDeclarationSyntax? configure = decl.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Configure");
            string? builderParam = configure?.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text;
            if (configure is null || builderParam is null) continue;

            ExtractFluentRelationships(context, sink, sm, path, configure, project,
                expr => expr is IdentifierNameSyntax id && id.Identifier.Text == builderParam ? configuredType : null);
        }

        return Task.CompletedTask;
    }

    // Recognizes modelBuilder.Entity<T>() as the root of a fluent chain, returning T.
    private static ITypeSymbol? EntityCallRoot(SemanticModel sm, ExpressionSyntax expr) =>
        expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Entity", TypeArgumentList.Arguments: [var typeArg, ..] } } }
            ? sm.GetSymbolInfo(typeArg).Symbol as ITypeSymbol
            : null;

    // Walks every .HasOne(...)/.HasMany(...) call in the given method body, tracing each one's fluent chain
    // back to its entity root via resolveRoot, and resolving the related type either from an explicit
    // generic argument (HasOne<TRelated>()) or from the navigation-property lambda (HasOne(e => e.Author)).
    private static void ExtractFluentRelationships(
        IAnalysisContext context, IDiscoverySink sink, SemanticModel sm, string path,
        SyntaxNode scope, string project, Func<ExpressionSyntax, ITypeSymbol?> resolveRoot)
    {
        foreach (InvocationExpressionSyntax inv in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax member) continue;
            string callName = member.Name.Identifier.Text;
            if (callName is not ("HasOne" or "HasMany")) continue;

            ITypeSymbol? fromType = ChainRoot(member.Expression, resolveRoot);
            if (fromType is null) continue;

            ITypeSymbol? toType = member.Name is GenericNameSyntax { TypeArgumentList.Arguments: [var typeArg, ..] }
                ? sm.GetSymbolInfo(typeArg).Symbol as ITypeSymbol
                : NavigationTargetType(fromType, inv);
            if (toType is null || !toType.Locations.Any(l => l.IsInSource) || SymbolEqualityComparer.Default.Equals(toType, fromType)) continue;

            Evidence ev = context.Evidence(path, Sym.Line(inv), $"{callName}({toType.Name})");
            KnowledgeIdentity fromId = Sym.TypeId(context, project, fromType);
            KnowledgeIdentity toId = Sym.TypeId(context, project, toType);
            // Fluent-API registration is an unambiguous fact regardless of whether the entity was also seen
            // via a DbSet<T> property or EntityAnalyzer — merged/deduped by identity in Validation either way.
            sink.Add(NodeDiscovery.Create(fromId, NodeKind.From("Entity"), new[] { ev }, Confidence.Full, Sym.Props(("name", fromType.Name))));
            sink.Add(NodeDiscovery.Create(toId, NodeKind.From("Entity"), new[] { ev }, Confidence.Full, Sym.Props(("name", toType.Name))));
            string relType = callName == "HasOne" ? "REFERENCES" : "HAS_MANY";
            sink.Add(RelationshipDiscovery.Create(RelationshipType.From(relType), fromId, toId, new[] { ev }, Confidence.From(0.9)));
        }
    }

    // Walks a fluent chain leftward (through .WithOne(...), .HasForeignKey(...), …) until resolveRoot
    // recognizes the base expression as the chain's originating entity.
    private static ITypeSymbol? ChainRoot(ExpressionSyntax expr, Func<ExpressionSyntax, ITypeSymbol?> resolveRoot)
    {
        ExpressionSyntax? current = expr;
        while (current is not null)
        {
            if (resolveRoot(current) is { } t) return t;
            current = current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } ? access.Expression : null;
        }

        return null;
    }

    // Resolves the related type from a navigation-property lambda, e.g. HasOne(e => e.Author).
    private static ITypeSymbol? NavigationTargetType(ITypeSymbol fromType, InvocationExpressionSyntax inv)
    {
        if (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is not SimpleLambdaExpressionSyntax
            { Body: MemberAccessExpressionSyntax { Name.Identifier.Text: var propName } }) return null;

        IPropertySymbol? prop = fromType.GetMembers(propName).OfType<IPropertySymbol>().FirstOrDefault();

        return prop is null ? null : Sym.ElementOfCollection(prop.Type) ?? prop.Type;
    }
}

/// <summary>Detects DI registrations, resolving interface/implementation type arguments semantically.</summary>
internal sealed class DependencyInjectionAnalyzer : IAnalyzer
{
    private static readonly string[] Methods = { "AddScoped", "AddSingleton", "AddTransient" };

    public string Name => "dependency-injection";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach (SyntaxTree tree in model.Trees)
        {
            SemanticModel sm = model.GetSemanticModel(tree);
            string path = model.PathOf(tree);
            foreach (InvocationExpressionSyntax inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax g }) continue;
                if (!Methods.Contains(g.Identifier.Text) || g.TypeArgumentList.Arguments.Count != 2) continue;

                (string iface, string ifaceProj) = Resolve(sm, g.TypeArgumentList.Arguments[0], project);
                (string impl, string implProj) = Resolve(sm, g.TypeArgumentList.Arguments[1], project);
                KnowledgeIdentity implId = context.NodeId(Sym.Seg("project", implProj), Sym.Seg("type", impl));
                KnowledgeIdentity ifaceId = context.NodeId(Sym.Seg("project", ifaceProj), Sym.Seg("type", iface));
                Evidence ev = context.Evidence(path, Sym.Line(inv), $"{g.Identifier.Text}<{iface},{impl}>");
                string implKind = impl.EndsWith("Repository", StringComparison.Ordinal) ? "Repository" : "Service";
                sink.Add(NodeDiscovery.Create(implId, NodeKind.From(implKind), new[] { ev }, Confidence.Full, Sym.Props(("name", impl.Split('.').Last()))));
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("IMPLEMENTS"), implId, ifaceId, new[] { ev }, Confidence.Full));
            }
        }

        return Task.CompletedTask;
    }

    private static (string Name, string Project) Resolve(SemanticModel sm, TypeSyntax type, string fallbackProject) =>
        sm.GetSymbolInfo(type).Symbol is ITypeSymbol s
            ? (Sym.Name(s), Sym.ProjectOf(s) ?? fallbackProject)
            : (type.ToString(), fallbackProject);
}

/// <summary>Detects the application entry point (the Program host component). Endpoint detection lives in
/// <see cref="MinimalApiAnalyzer"/>.</summary>
internal sealed class ProgramAnalyzer : IAnalyzer
{
    public string Name => "program";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        foreach (SyntaxTree tree in model.Trees)
        {
            string path = model.PathOf(tree);
            if (Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
                sink.Add(NodeDiscovery.Create(context.NodeId(Sym.Seg("project", context.Artifact.Name), Sym.Seg("component", "Program")),
                    NodeKind.From("Component"), new[] { context.Evidence(path, 1, "Program") }, Confidence.Full, Sym.Props(("name", "Program"), ("role", "host"))));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects Minimal API endpoints, covering the two common shapes beyond attribute-routed controllers:
/// (1) fluent groups — <c>var api = x.MapGroup("api/catalog"); api.MapGet("/items/{id}", Handler)</c>
/// (eShop-style), and (2) endpoint-group classes — an <c>IEndpointGroup</c>/<c>EndpointGroupBase</c> whose
/// <c>Map</c> method calls <c>group.MapPost(Handler, "{id}")</c>, mapped to <c>/api/{ClassName}</c>
/// (Clean-Architecture-style). Route constraints (<c>{id:int}</c>) are normalized to <c>{id}</c>.
/// </summary>
internal sealed class MinimalApiAnalyzer : IAnalyzer
{
    private static readonly System.Text.RegularExpressions.Regex Constraint =
        new(@"\{(\w+)[^{}]*\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    public string Name => "minimal-api";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        foreach (SyntaxTree tree in model.Trees)
        {
            SyntaxNode root = tree.GetRoot();
            string path = model.PathOf(tree);

            // (2) Endpoint-group classes: /api/{ClassName} prefix; their Map* calls are handled here.
            var groupClasses = new HashSet<InvocationExpressionSyntax>();
            foreach (TypeDeclarationSyntax type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                bool isGroup = type.BaseList?.Types.Any(t => t.Type.ToString().Split('.').Last() is "IEndpointGroup" or "EndpointGroupBase") ?? false;
                if (!isGroup) continue;
                string prefix = "/api/" + type.Identifier.Text;
                foreach (InvocationExpressionSyntax inv in type.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (MapVerb(inv) is not string verb) continue;
                    groupClasses.Add(inv);
                    string route = Combine(prefix, LiteralArg(inv) ?? "");
                    Emit(context, sink, path, inv, verb, route, HandlerArg(inv));
                }
            }

            // (1) Fluent groups: map group variables to their MapGroup("prefix").
            var groupPrefix = new Dictionary<string, string>();
            foreach (VariableDeclaratorSyntax v in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                string? prefix = v.Initializer?.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                    .Where(i => MemberName(i) == "MapGroup").Select(LiteralArg).FirstOrDefault(p => p is not null);
                if (prefix is not null) groupPrefix[v.Identifier.Text] = prefix;
            }

            foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (groupClasses.Contains(inv)) continue;                 // already handled as a group class
                if (MapVerb(inv) is not string verb) continue;
                string? literal = LiteralArg(inv);
                if (literal is null) continue;                            // needs an explicit route literal
                string prefix = inv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax id }
                    && groupPrefix.TryGetValue(id.Identifier.Text, out string? p) ? p : "";
                Emit(context, sink, path, inv, verb, Combine(prefix, literal), HandlerArg(inv));
            }
        }

        return Task.CompletedTask;
    }

    private void Emit(IAnalysisContext context, IDiscoverySink sink, string path, InvocationExpressionSyntax inv, string verb, string route, string? handler)
    {
        route = Constraint.Replace(route, "{$1}");
        Evidence ev = context.Evidence(path, Sym.Line(inv), handler ?? verb);
        var props = new List<(string, string)> { ("verb", verb), ("route", route) };
        if (handler is not null) props.Add(("action", handler));
        if (ChainedAuthorization(inv) is { } auth) props.Add(("authorize", auth));
        sink.Add(NodeDiscovery.Create(context.AppNodeId(Sym.Seg("endpoint", $"{verb} {route}")),
            NodeKind.From("Endpoint"), new[] { ev }, Confidence.From(0.9), Sym.Props(props.ToArray())));
    }

    // Minimal APIs attach authorization via a fluent suffix on the same statement, e.g.
    // app.MapGet("/x", Handler).RequireAuthorization("AdminOnly") or .AllowAnonymous(). Walk outward through
    // the chain (skipping intermediate calls like WithName/Produces) looking for either — the same fact
    // ControllerAnalyzer already captures for attribute-routed controllers via [Authorize]/[AllowAnonymous].
    private static string? ChainedAuthorization(InvocationExpressionSyntax mapCall)
    {
        SyntaxNode? current = mapCall;
        while (current?.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax next } access)
        {
            string name = access.Name.Identifier.Text;
            if (name == "AllowAnonymous") return "AllowAnonymous";
            if (name == "RequireAuthorization")
            {
                string? policy = next.ArgumentList.Arguments.Select(a => a.Expression)
                    .OfType<LiteralExpressionSyntax>().Select(l => l.Token.ValueText).FirstOrDefault();

                return policy is not null ? $"Authorize (Policy: {policy})" : "Authorize";
            }
            current = next;
        }

        return null;
    }

    private static string? MapVerb(InvocationExpressionSyntax inv)
    {
        string? name = MemberName(inv);
        if (name is null || !name.StartsWith("Map", StringComparison.Ordinal)) return null;
        string verb = name["Map".Length..];

        return verb is "Get" or "Post" or "Put" or "Delete" or "Patch" ? verb.ToUpperInvariant() : null;
    }

    private static string? MemberName(InvocationExpressionSyntax inv) =>
        (inv.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;

    private static string? LiteralArg(InvocationExpressionSyntax inv) =>
        inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
            .Select(l => l.Token.ValueText).FirstOrDefault(v => v is not null);

    // The handler is the method-group argument (an identifier that isn't the route string literal).
    private static string? HandlerArg(InvocationExpressionSyntax inv) =>
        inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.Text).FirstOrDefault();

    private static string Combine(string prefix, string path)
    {
        string joined = $"{prefix.Trim('/')}/{path.Trim('/')}".Trim('/');
        while (joined.Contains("//")) joined = joined.Replace("//", "/");

        return "/" + joined;
    }
}

/// <summary>Reads appsettings*.json and emits Configuration + DataStore facts for connection strings.</summary>
internal sealed class ConfigurationAnalyzer : IAnalyzer
{
    // ASP.NET Core reads appsettings via a JSONC-tolerant reader, so real files routinely carry
    // // comments and trailing commas. Parse the same way rather than rejecting them as invalid.
    private static readonly JsonDocumentOptions JsoncTolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string Name => "configuration";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(context.Artifact.Path) ?? context.Artifact.Path;
        string project = context.Artifact.Name;

        foreach (string file in Directory.EnumerateFiles(dir, "appsettings*.json", SearchOption.TopDirectoryOnly))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file), JsoncTolerant); }
            catch (JsonException ex) { sink.Report(Aip.Core.Domain.Diagnostic.Warning($"Invalid JSON in {Path.GetFileName(file)}: {ex.Message}", Name)); continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("ConnectionStrings", out JsonElement connections)) continue;
                foreach (JsonProperty c in connections.EnumerateObject())
                {
                    Evidence ev = context.Evidence(file, null, c.Name);
                    sink.Add(NodeDiscovery.Create(context.NodeId(Sym.Seg("project", project), Sym.Seg("config", $"ConnectionStrings:{c.Name}")),
                        NodeKind.From("Configuration"), new[] { ev }, Confidence.Full, Sym.Props(("name", c.Name))));
                    sink.Add(NodeDiscovery.Create(context.NodeId(Sym.Seg("datastore", c.Name)),
                        NodeKind.From("DataStore"), new[] { ev }, Confidence.Full, Sym.Props(("name", c.Name), ("kind", "connection-string"))));
                }
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Maps a package id fragment to a technical capability — grounded facts for the technology stack.</summary>
internal static class CapabilityRules
{
    // (package-id fragment, category, friendly name) — case-insensitive "contains" match.
    public static readonly (string Fragment, string Category, string Name)[] Rules =
    {
        ("Microsoft.AspNetCore",          "Web / API",         "ASP.NET Core"),
        ("Swashbuckle",                   "API Documentation", "Swagger / Swashbuckle"),
        ("Microsoft.AspNetCore.OpenApi",  "API Documentation", "OpenAPI"),
        ("MediatR",                       "Application",       "MediatR (CQRS/mediator)"),
        ("Dapper",                        "Data Access",       "Dapper"),
        ("EntityFrameworkCore",           "Data Access",       "Entity Framework Core"),
        ("MongoDB.Driver",                "Data Access",       "MongoDB Driver"),
        ("Microsoft.Data.SqlClient",      "Database",          "SQL Server"),
        ("System.Data.SqlClient",         "Database",          "SQL Server"),
        ("Npgsql",                        "Database",          "PostgreSQL"),
        ("Pomelo.EntityFrameworkCore.MySql","Database",        "MySQL"),
        ("Sqlite",                        "Database",          "SQLite"),
        ("Microsoft.Azure.Cosmos",        "Database",          "Azure Cosmos DB"),
        ("Azure.Storage.Blobs",           "Cloud Storage",     "Azure Blob Storage"),
        ("Azure.Storage.Queues",          "Messaging",         "Azure Queue Storage"),
        ("AWSSDK.S3",                     "Cloud Storage",     "AWS S3"),
        ("RabbitMQ.Client",               "Messaging",         "RabbitMQ"),
        ("MassTransit",                   "Messaging",         "MassTransit"),
        ("Azure.Messaging.ServiceBus",    "Messaging",         "Azure Service Bus"),
        ("Confluent.Kafka",               "Messaging",         "Apache Kafka"),
        ("StackExchange.Redis",           "Caching",           "Redis"),
        ("Microsoft.Extensions.Caching",  "Caching",           "Distributed / in-memory cache"),
        ("Aspire",                        "Cloud",             ".NET Aspire"),
        ("Azure.",                        "Cloud",             "Azure SDK"),
        ("AWSSDK.",                       "Cloud",             "AWS SDK"),
        ("Authentication.JwtBearer",      "Authentication",    "JWT Bearer authentication"),
        ("Microsoft.AspNetCore.Identity", "Authentication",    "ASP.NET Core Identity"),
        ("IdentityServer",                "Authentication",    "Duende/IdentityServer"),
        ("OpenIdConnect",                 "Authentication",    "OpenID Connect"),
        ("Microsoft.Identity.Web",        "Authentication",    "Microsoft Entra ID (Azure AD)"),
        ("Okta.AspNetCore",               "Authentication",    "Okta"),
        ("Auth0.AspNetCore.Authentication","Authentication",   "Auth0"),
        ("BCrypt",                        "Security",          "BCrypt password hashing"),
        ("SemanticKernel",               "AI / LLM",          "Semantic Kernel"),
        ("Azure.AI",                      "AI / LLM",          "Azure AI"),
        ("OpenAI",                        "AI / LLM",          "OpenAI"),
        ("FluentValidation",              "Validation",        "FluentValidation"),
        ("AutoMapper",                    "Mapping",           "AutoMapper"),
        ("Mapster",                       "Mapping",           "Mapster"),
        ("Serilog",                       "Logging",           "Serilog"),
        ("NLog",                          "Logging",           "NLog"),
        ("Audit.NET",                     "Auditing",          "Audit.NET"),
        ("AspNetCore.HealthChecks",       "Observability",     "HealthChecks"),
        ("OpenTelemetry",                "Observability",     "OpenTelemetry"),
        ("Polly",                         "Resilience",        "Polly"),
        ("Hangfire",                      "Background Jobs",   "Hangfire"),
        ("Quartz",                        "Background Jobs",   "Quartz.NET"),
        ("NServiceBus",                   "Messaging",         "NServiceBus"),
        ("blazor",                        "Frontend",          "Blazor"),
        ("xunit",                         "Testing",           "xUnit"),
        ("nunit",                         "Testing",           "NUnit"),
        ("Moq",                           "Testing",           "Moq"),
    };
}

/// <summary>
/// Reads the project's own <c>.csproj</c>: emits a Project node (name, target framework, output kind) and
/// a Technology node per detected package capability. This is what makes the technology stack real rather
/// than inferred from node kinds.
/// </summary>
internal sealed class PackageAnalyzer : IAnalyzer
{
    public string Name => "packages";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        string csproj = context.Artifact.Path;
        string project = context.Artifact.Name;
        if (!File.Exists(csproj)) return Task.CompletedTask;

        XDocument doc;
        try { doc = XDocument.Load(csproj); }
        catch { return Task.CompletedTask; }

        var packages = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();

        string? tfm = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")?.Value;
        string? outputType = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value;
        string sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";

        // A project that references a test framework is a test project (kept distinct from app projects).
        bool isTest = packages.Any(p => p.Contains("xunit", StringComparison.OrdinalIgnoreCase) || p.Contains("nunit", StringComparison.OrdinalIgnoreCase)
            || p.Contains("MSTest", StringComparison.OrdinalIgnoreCase) || p.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        string kind = isTest ? "test"
            : sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ? "web"
            : string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ? "executable" : "library";

        var projProps = new List<(string, string)> { ("name", project), ("kind", kind) };
        if (tfm is not null) projProps.Add(("framework", tfm));
        Evidence pev = context.Evidence(csproj, null, project);
        sink.Add(NodeDiscovery.Create(context.NodeId(Sym.Seg("project", project)),
            NodeKind.From("Project"), new[] { pev }, Confidence.Full, Sym.Props(projProps.ToArray())));

        foreach (string package in packages)
        {
            foreach ((string fragment, string category, string techName) in CapabilityRules.Rules)
            {
                if (!package.Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
                Evidence ev = context.Evidence(csproj, null, package);
                sink.Add(NodeDiscovery.Create(context.AppNodeId(Sym.Seg("technology", techName)),
                    NodeKind.From("Technology"), new[] { ev }, Confidence.Full,
                    Sym.Props(("name", techName), ("category", category), ("package", package))));
                break;   // rules are ordered specific→generic; the most specific capability wins (no "Azure SDK" noise)
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds the call graph from constructor injection: any in-source class that takes another in-source type
/// in its constructor DEPENDS_ON it. Not restricted to Controller/Service/Repository — that allowlist
/// silently dropped edges for CQRS handlers, validators, message consumers, and filters (kinds this same
/// plugin already detects elsewhere), even though constructor injection is a structural fact independent of
/// architectural role. DTOs/entities rarely take injected in-source dependencies in their constructors, so
/// opening this up doesn't trade precision for the recall gained. Same-project edges resolve; cross-project
/// references (resolved in a different analysis unit) are proposed and dropped honestly by the validation gate.
/// </summary>
internal sealed class DependencyAnalyzer : IAnalyzer
{
    public string Name => "call-graph";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;

            KnowledgeIdentity fromId = context.NodeId(Sym.Seg("project", Sym.ProjectOf(symbol) ?? project), Sym.Seg("type", Sym.Name(symbol)));

            foreach (IMethodSymbol ctor in symbol.Constructors.Where(c => !c.IsStatic && !c.IsImplicitlyDeclared))
                foreach (IParameterSymbol p in ctor.Parameters)
                {
                    if (p.Type is not INamedTypeSymbol dep) continue;
                    if (!dep.Locations.Any(l => l.IsInSource)) continue;          // in-source (now solution-wide) dependencies
                    if (SymbolEqualityComparer.Default.Equals(dep, symbol)) continue;

                    // Target the dependency in the project that declares it (may be another project in the solution).
                    KnowledgeIdentity toId = context.NodeId(Sym.Seg("project", Sym.ProjectOf(dep) ?? project), Sym.Seg("type", Sym.Name(dep)));
                    Evidence ev = context.Evidence(path, Sym.Line(decl), p.Name);
                    sink.Add(RelationshipDiscovery.Create(RelationshipType.From("DEPENDS_ON"), fromId, toId, new[] { ev }, Confidence.From(0.9)));
                }
        }

        return Task.CompletedTask;
    }
}
