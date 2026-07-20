using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Aip.Abstractions.Ai;
using Aip.Abstractions.History;
using Aip.Abstractions.Projections;
using Aip.Core.Abstractions;
using Aip.Core.Domain;

namespace Aip.Projections;

/// <summary>
/// Classifies an AI call failure by exception TYPE (never by parsing <c>ex.Message</c>), so "why" survives
/// structurally into the AiFallbackEvents table and the mirrored log line (see EfAiFallbackStore) instead
/// of every call site re-deriving it. Shared by every AI-assisted projection artifact — today
/// <see cref="DocumentationProjection"/>'s pages and <see cref="VersionChangelogGenerator"/>'s changelog.
/// </summary>
internal static class AiFallbackClassification
{
    public static string Classify(Exception ex) => ex switch
    {
        AiProviderException { StatusCode: 429 } => "RateLimited",
        AiProviderException ape => $"ProviderError{(ape.StatusCode is int code ? $"({code})" : "")}",
        OperationCanceledException => "Timeout",
        HttpRequestException => "NetworkError",
        JsonException => "MalformedResponse",
        NotSupportedException => "ProviderUnavailable",
        _ => "Unknown"
    };
}

/// <summary>
/// Generates the documentation set entirely from the Knowledge Model (the snapshot). It never inspects
/// source code. The set is organized like an engineering spec — a <b>Product Specification</b> (overview,
/// features, use-cases) and a <b>Technical Specification</b> (architecture, API, technology, data,
/// frontend). Rendering is deterministic and descriptive; when an AI Platform is available, the narrative
/// pages are rewritten from the same grounded view model (the AI sees the model, never repositories).
/// </summary>
internal sealed class DocumentationProjection : IProjection
{
    private readonly IAiPlatform _ai;
    private readonly IContextBuilder _context;
    private readonly IAiFallbackStore _fallback;

    public DocumentationProjection(IAiPlatform ai, IContextBuilder context, IAiFallbackStore fallback)
    {
        _ai = ai;
        _context = context;
        _fallback = fallback;
    }

    public string Name => "documentation";

    private const string Product = "product-specification";
    private const string Technical = "technical-specification";

    public async Task<ProjectionResult> ProjectAsync(ProjectionRequest request, CancellationToken ct = default)
    {
        Snapshot s = request.Snapshot;
        string app = s.Application.Value;
        IReadOnlyList<string> repos = request.Repositories;

        var artifacts = new List<ProjectionArtifact>
        {
            // Product Specification — enhanced by AI when available (pure narrative).
            await Page(app, repos, $"{Product}/overview.md",   "product-overview",   1, Overview(s, app),      ct, ai: true),
            await Page(app, repos, $"{Product}/features.md",    "product-features",   2, Features(s, app),      ct, ai: true),
            await Page(app, repos, $"{Product}/use-cases.md",   "product-use-cases",  3, UseCases(s, app),      ct, ai: true),

            // Technical Specification — AI adds narrative framing around every page; the AI system prompt
            // (Aip.Ai/AiModule.cs PromptTemplates.System) requires every Markdown table and ```mermaid```
            // block be preserved verbatim, so architecture/api-reference get prose without losing exactness.
            // Deterministic here only ever means "this run's AI attempt fell back" (see RecordFallbackAsync)
            // — never a standing exception for these two pages.
            await Page(app, repos, $"{Technical}/architecture.md",     "tech-architecture", 1, Architecture(s, app), ct, ai: true),
            await Page(app, repos, $"{Technical}/api-reference.md",    "tech-api",          2, Api(s, app), ct, ai: true),
            await Page(app, repos, $"{Technical}/technology-stack.md", "tech-stack",        3, Technologies(s, app), ct, ai: true),
        };

        if (Nodes(s, "DataStore").Any() || Nodes(s, "Entity").Any())
            artifacts.Add(await Page(app, repos, $"{Technical}/data-and-storage.md", "tech-data", 4, Database(s, app), ct, ai: true));

        if (HasSecurity(s))
            artifacts.Add(await Page(app, repos, $"{Technical}/security.md", "tech-security", 6, Security(s, app), ct, ai: true));

        // Only when there's a real frontend — the backend "Component" (Program host) must not trigger this.
        if (Nodes(s, "UIComponent").Any() || Nodes(s, "Route").Any() || Nodes(s, "UIService").Any())
            artifacts.Add(await FrontendPage(s, app, repos, ct));

        return new ProjectionResult(Name, artifacts);
    }

    // Frontend as three independent agent calls (roles/nav, screens, component inventory) instead of one —
    // each group is only sent to the AI when it has real grounded facts, so a topic with nothing to say
    // never gets a chance to be padded. See the three Frontend*() group builders above.
    private async Task<ProjectionArtifact> FrontendPage(Snapshot s, string app, IReadOnlyList<string> repos, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Frontend\n");

        (string Markdown, bool HasContent)[] groups =
        {
            FrontendRolesAndNav(s, app),
            FrontendScreens(s, app),
            FrontendComponents(s, app),
        };
        string[] templates = { "tech-frontend-roles", "tech-frontend-screens", "tech-frontend-components" };
        bool anyAi = false;

        for (int i = 0; i < groups.Length; i++)
        {
            (string markdown, bool hasContent) = groups[i];
            if (!hasContent) continue;   // no grounded facts for this topic — skip it entirely, no AI call

            (string rendered, bool aiUsed) = await RenderSection(templates[i], app, repos, markdown, ct);
            sb.AppendLine(rendered);
            sb.AppendLine();
            anyAi |= aiUsed;
        }

        return new ProjectionArtifact($"{Technical}/frontend.md", "text/markdown", sb.ToString(), 5, anyAi);
    }

    private async Task<(string Text, bool AiUsed)> RenderSection(string template, string app, IReadOnlyList<string> repos, string deterministic, CancellationToken ct)
    {
        if (_ai.IsAvailable)
        {
            try
            {
                var values = new Dictionary<string, string> { ["app"] = app, ["model"] = _context.Build(deterministic) };

                return (await _ai.RenderAsync(template, values, ct), true);
            }
            catch (Exception ex) { await RecordFallbackAsync(app, repos, template, ex, ct); }
        }

        return (deterministic, false);
    }

    private async Task<ProjectionArtifact> Page(string app, IReadOnlyList<string> repos, string path, string template, int order, string deterministic, CancellationToken ct, bool ai = false)
    {
        string content = deterministic;
        bool aiWritten = false;
        if (ai && _ai.IsAvailable)
        {
            try
            {
                var values = new Dictionary<string, string>
                {
                    ["app"] = app,
                    ["model"] = _context.Build(deterministic)
                };
                content = await _ai.RenderAsync(template, values, ct);
                aiWritten = true;
            }
            catch (Exception ex) { await RecordFallbackAsync(app, repos, template, ex, ct); }
        }

        return new ProjectionArtifact(path, "text/markdown", content, order, aiWritten);
    }

    // The one place an AI failure becomes a fallback-to-deterministic decision.
    private async Task RecordFallbackAsync(string app, IReadOnlyList<string> repos, string section, Exception ex, CancellationToken ct) =>
        await _fallback.RecordAsync(new AiFallbackEvent(app, repos, section, AiFallbackClassification.Classify(ex), ex.Message, DateTimeOffset.UtcNow), ct);

    // ======================= Product Specification =======================

    private string Overview(Snapshot s, string app)
    {
        int endpoints = Nodes(s, "Endpoint").Count();
        int controllers = Nodes(s, "Controller").Count();
        int services = Nodes(s, "Service").Count();
        int stores = Nodes(s, "DataStore").Count();
        int ui = Nodes(s, "UIComponent").Count() + Nodes(s, "Component").Count();

        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Product Overview\n");

        sb.AppendLine($"## What is {app}?\n");
        sb.AppendLine($"{app} is an application described by {s.Nodes.Count} knowledge node(s) and " +
                      $"{s.Relationships.Count} relationship(s) in the Application Intelligence Platform's Knowledge Model. " +
                      Sentence(endpoints > 0, $"It exposes {endpoints} HTTP endpoint(s) through {controllers} controller(s).") +
                      Sentence(services > 0, $" Business logic is organized across {services} service(s).") +
                      Sentence(stores > 0, $" Application data is persisted in {stores} data store(s).") +
                      Sentence(ui > 0, $" It presents a frontend built from {ui} UI component(s)."));
        sb.AppendLine();

        sb.AppendLine("## What it does\n");
        var caps = CapabilityLines(s).ToList();
        if (caps.Count > 0) foreach (string c in caps) sb.AppendLine($"- {c}");
        else sb.AppendLine("- _No externally exposed capabilities were detected in the model._");
        sb.AppendLine();

        sb.AppendLine("## Building blocks\n");
        sb.AppendLine("| Concept | Count | Role |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (IGrouping<string, KnowledgeNode> g in s.Nodes.GroupBy(n => n.Kind.Value).OrderByDescending(g => g.Count()))
            sb.AppendLine($"| {g.Key} | {g.Count()} | {RoleOf(g.Key)} |");

        sb.AppendLine($"\n_Generated from the Knowledge Model — no source code was read to produce this page._");

        return sb.ToString();
    }

    private string Features(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Features\n");
        sb.AppendLine("Capabilities the application offers, grouped by the controller that exposes them.\n");

        var endpoints = Nodes(s, "Endpoint").ToList();
        if (endpoints.Count == 0) { sb.AppendLine("_No exposed endpoints were detected in the model._"); return sb.ToString(); }

        foreach (IGrouping<string, KnowledgeNode> group in endpoints
            .GroupBy(e => OwnerOf(s, e) ?? "General")
            .OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {Humanize(StripSuffix(group.Key, "Controller"))}\n");
            foreach (KnowledgeNode e in group.OrderBy(n => Prop(n, "route")))
                sb.AppendLine($"- **{Operation(e)}** — `{Prop(e, "verb")} {Prop(e, "route")}`");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string UseCases(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Use Cases\n");
        sb.AppendLine("Scenarios inferred from the operations the application exposes.\n");

        var endpoints = Nodes(s, "Endpoint").OrderBy(n => Prop(n, "route")).ToList();
        if (endpoints.Count == 0) { sb.AppendLine("_No use cases could be inferred — no endpoints in the model._"); return sb.ToString(); }

        int i = 1;
        foreach (KnowledgeNode e in endpoints)
        {
            string? raw = OwnerOf(s, e);
            string owner = raw is null ? "the application" : Humanize(StripSuffix(raw, "Controller"));
            sb.AppendLine($"{i++}. **{Operation(e)}** — a client calls `{Prop(e, "verb")} {Prop(e, "route")}`, handled by {owner}.");
        }

        return sb.ToString();
    }

    // ======================= Technical Specification =======================

    private string Architecture(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Architecture\n");

        sb.AppendLine("## Overview\n");
        var kinds = s.Nodes.Select(n => n.Kind.Value).Distinct().ToHashSet();
        sb.AppendLine("The application is composed of the following layers, inferred from the Knowledge Model:");
        sb.AppendLine();
        if (kinds.Contains("Controller") || kinds.Contains("Endpoint"))
            sb.AppendLine("- **API / presentation** — controllers receive HTTP requests and expose endpoints to clients.");
        if (kinds.Contains("Service") || kinds.Contains("Interface"))
            sb.AppendLine("- **Application / domain** — services encapsulate business logic behind interfaces.");
        if (kinds.Contains("Repository"))
            sb.AppendLine("- **Data access** — repositories mediate persistence for domain types.");
        if (kinds.Contains("DataStore") || kinds.Contains("Entity"))
            sb.AppendLine("- **Data / persistence** — data stores and entities hold the application's state.");
        if (kinds.Contains("UIComponent") || kinds.Contains("Route") || kinds.Contains("Component"))
            sb.AppendLine("- **Frontend** — UI components and routes make up the client the user interacts with.");
        sb.AppendLine();

        var patterns = DetectPatterns(s).ToList();
        if (patterns.Count > 0)
        {
            sb.AppendLine("## Detected patterns\n");
            foreach ((string name, string evidence) in patterns)
                sb.AppendLine($"- **{name}** — {evidence}");
            sb.AppendLine();
        }

        AppendCrossCutting(sb, s);
        AppendMessaging(sb, s);
        AppendCqrs(sb, s);

        var projects = Nodes(s, "Project").ToList();
        if (projects.Count > 0)
        {
            sb.AppendLine("## Solution structure\n");
            sb.AppendLine($"The application is made up of {projects.Count} project(s):\n");
            foreach (KnowledgeNode p in projects.OrderBy(n => Prop(n, "name")))
            {
                string fw = Prop(p, "framework") is { } f ? $", {f}" : "";
                sb.AppendLine($"- **{Prop(p, "name")}** ({Prop(p, "kind") ?? "library"}{fw})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Components\n");
        foreach (string kind in new[] { "Controller", "Service", "Repository", "Interface", "DataStore", "Entity", "UIComponent", "UIService", "Hook", "Context", "Guard", "Interceptor", "Component", "Route" })
        {
            var items = Nodes(s, kind).ToList();
            if (items.Count == 0) continue;
            var names = items.Select(n => Prop(n, "name") ?? Label(n.Identity)).Distinct().OrderBy(n => n).ToList();
            sb.AppendLine($"### {kind} ({names.Count})");
            sb.AppendLine($"_{RoleOf(kind)}_\n");
            foreach (string name in names) sb.AppendLine($"- {name}");
            sb.AppendLine();
        }

        sb.AppendLine("## Component relationships\n");
        if (s.Relationships.Count == 0) sb.AppendLine("_No relationships were resolved between components._\n");
        else
        {
            sb.AppendLine("```mermaid");
            sb.AppendLine("graph LR");
            foreach (Relationship r in s.Relationships.Take(50))
                sb.AppendLine($"  {Safe(Label(r.From))}[\"{Label(r.From)}\"] -->|{r.Type.Value}| {Safe(Label(r.To))}[\"{Label(r.To)}\"]");
            sb.AppendLine("```\n");
            sb.AppendLine("Key connections:");
            foreach (Relationship r in s.Relationships.Take(20))
                sb.AppendLine($"- `{Label(r.From)}` **{r.Type.Value}** `{Label(r.To)}`");
        }

        return sb.ToString();
    }

    private string Api(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — API Reference\n");
        var endpoints = Nodes(s, "Endpoint").ToList();
        if (endpoints.Count == 0) { sb.AppendLine("_No HTTP endpoints were detected in the model._"); return sb.ToString(); }

        sb.AppendLine($"{endpoints.Count} endpoint(s) are exposed by the application.\n");
        foreach (IGrouping<string, KnowledgeNode> group in endpoints.GroupBy(e => OwnerOf(s, e) ?? "General").OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {group.Key}\n");
            sb.AppendLine("| Method | Route | Description |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (KnowledgeNode e in group.OrderBy(n => Prop(n, "route")))
                sb.AppendLine($"| {Prop(e, "verb")} | `{Prop(e, "route")}` | {Operation(e)} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string Technologies(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Technology Stack\n");

        // Prefer technologies detected from package references (grouped by capability category).
        var techNodes = Nodes(s, "Technology").ToList();
        if (techNodes.Count > 0)
        {
            sb.AppendLine("Detected from package references and, where found, how each is actually used in the code.\n");
            foreach (IGrouping<string, KnowledgeNode> cat in techNodes
                .GroupBy(n => Prop(n, "category") ?? "Other").OrderBy(g => g.Key))
            {
                sb.AppendLine($"### {cat.Key}");
                foreach (KnowledgeNode t in cat.DistinctBy(n => Prop(n, "name")).OrderBy(n => Prop(n, "name")))
                {
                    string line = $"- **{Prop(t, "name")}**" + (Prop(t, "package") is { } pkg ? $" — `{pkg}`" : "");
                    if (Prop(t, "usage") is { Length: > 0 } usage) line += $" — **used by** {usage}";
                    sb.AppendLine(line);
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Fallback: infer from the shape of the model when no package data is available.
        var inferred = new List<string>();
        if (Nodes(s, "Controller").Any() || Nodes(s, "Endpoint").Any()) inferred.Add("ASP.NET Core");
        if (Nodes(s, "DataStore").Any(n => Prop(n, "kind") == "ef-dbcontext")) inferred.Add("Entity Framework Core");
        if (Nodes(s, "UIComponent").Any() || Nodes(s, "Route").Any()) inferred.Add("Angular");
        if (inferred.Count == 0) { sb.AppendLine("_No technologies could be inferred from the model._"); return sb.ToString(); }
        foreach (string t in inferred.Distinct()) sb.AppendLine($"- {t}");

        return sb.ToString();
    }

    private string Database(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Data & Storage\n");

        // Storage landscape — what engine(s) and object-storage technologies are actually configured,
        // pulled from the SAME Technology facts the tech-stack page uses (categories "Database" and
        // "Cloud Storage" from CapabilityRules). Generic: works for any provider AIP already recognizes
        // (SQL Server, PostgreSQL, MongoDB, Cosmos DB, SQLite, Blob Storage, S3, ...), not hardcoded to one.
        var storageTech = Nodes(s, "Technology").Where(t => Prop(t, "category") is "Database" or "Cloud Storage").ToList();
        if (storageTech.Count > 0)
        {
            sb.AppendLine("## Storage landscape\n");
            foreach (KnowledgeNode t in storageTech.OrderBy(n => Prop(n, "category")).ThenBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(t, "name")}** ({Prop(t, "category")})");
            sb.AppendLine();
        }

        var stores = Nodes(s, "DataStore").ToList();
        if (stores.Count > 0)
        {
            sb.AppendLine("## Data stores\n");
            foreach (KnowledgeNode store in stores)
            {
                sb.AppendLine($"### {Prop(store, "name") ?? Label(store.Identity)}");
                string? kind = Prop(store, "kind");
                if (kind is not null) sb.AppendLine($"_Kind: {kind}_\n");
                var owned = s.Relationships.Where(r => r.Type.Value == "OWNS" && r.From.Equals(store.Identity)).ToList();
                if (owned.Count > 0)
                {
                    sb.AppendLine("Owns:");
                    foreach (Relationship owns in owned) sb.AppendLine($"- {Label(owns.To)}");
                }
                sb.AppendLine();
            }
        }

        var entities = Nodes(s, "Entity").ToList();
        if (entities.Count > 0)
        {
            var entityRels = entities.ToDictionary(e => e.Identity,
                e => s.Relationships.Where(r => r.From.Equals(e.Identity) && r.Type.Value is "HAS_MANY" or "REFERENCES").ToList());

            // A mermaid ER diagram from the same HAS_MANY/REFERENCES facts already used per-entity below —
            // gives a reader the whole shape of the model at a glance before the entity-by-entity detail.
            var diagramEdges = entityRels.Values.SelectMany(r => r)
                .Select(r => r.Type.Value == "HAS_MANY"
                    ? $"    {SanitizeErName(Label(r.From))} ||--o{{ {SanitizeErName(Label(r.To))} : \"has many\""
                    : $"    {SanitizeErName(Label(r.From))} }}o--|| {SanitizeErName(Label(r.To))} : \"references\"")
                .Distinct().ToList();
            if (diagramEdges.Count > 0)
            {
                sb.AppendLine("## Entity relationships\n");
                sb.AppendLine("```mermaid");
                sb.AppendLine("erDiagram");
                foreach (string edge in diagramEdges) sb.AppendLine(edge);
                sb.AppendLine("```\n");
            }

            // Which service/class actually touches each entity — reuses the SAME "usage" facts the
            // tech-stack page renders (TechnologyUsageAnalyzer's generic entity-linkage), so an entity's
            // write-up can say who creates/queries/updates it instead of only listing its own fields and
            // relationships in isolation.
            Dictionary<string, HashSet<string>> usedBy = EntityUsage(s);

            sb.AppendLine($"## Domain model\n");
            sb.AppendLine($"The application manages {entities.Count} domain entit{(entities.Count == 1 ? "y" : "ies")}.\n");
            foreach (KnowledgeNode e in entities.OrderBy(n => Prop(n, "name") ?? Label(n.Identity)))
            {
                string name = Prop(e, "name") ?? Label(e.Identity);
                sb.AppendLine($"### {name}");
                if (Prop(e, "key") is { } key) sb.AppendLine($"_Identified by `{key}`._\n");

                if (Prop(e, "fields") is { Length: > 0 } fields)
                {
                    sb.AppendLine("| Field | Type |");
                    sb.AppendLine("| --- | --- |");
                    foreach (string f in fields.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int c = f.IndexOf(':');
                        sb.AppendLine(c > 0 ? $"| {f[..c].Trim()} | {f[(c + 1)..].Trim()} |" : $"| {f.Trim()} |  |");
                    }
                    sb.AppendLine();
                }

                if (Prop(e, "validated") is { Length: > 0 } validated)
                    sb.AppendLine($"_Validated (DataAnnotations): {validated}._\n");

                if (entityRels.TryGetValue(e.Identity, out List<Relationship>? rels) && rels.Count > 0)
                {
                    sb.AppendLine("Relationships:");
                    foreach (Relationship r in rels)
                        sb.AppendLine($"- {(r.Type.Value == "HAS_MANY" ? "has many" : "references")} **{Label(r.To)}**");
                    sb.AppendLine();
                }

                if (usedBy.TryGetValue(name, out HashSet<string>? classes) && classes.Count > 0)
                    sb.AppendLine($"_Used by: {string.Join(", ", classes.OrderBy(c => c))}._\n");
            }
        }

        return sb.ToString();
    }

    private static string SanitizeErName(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    // Parses TechnologyUsageAnalyzer's "usage" string (e.g. "ContractService (Add(Contract), FindAsync
    // (Contract))") back into an entity-name → owning-class-names map. Purely deterministic post-processing
    // of a fact that already exists in the graph — no new analyzer, works for any technology/entity shape.
    private static Dictionary<string, HashSet<string>> EntityUsage(Snapshot s)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var classSegment = new Regex(@"^(?<cls>[\w.]+)\s*\((?<ops>.*)\)$", RegexOptions.Compiled);
        var opSubject = new Regex(@"^\w+\((?<subject>\w+)\)$", RegexOptions.Compiled);

        foreach (KnowledgeNode tech in s.Nodes.Where(n => n.Kind.Value == "Technology"))
        {
            string? usage = Prop(tech, "usage");
            if (string.IsNullOrEmpty(usage)) continue;

            foreach (string segment in usage.Split("; ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                Match cm = classSegment.Match(segment);
                if (!cm.Success) continue;
                string cls = cm.Groups["cls"].Value;

                foreach (string op in cm.Groups["ops"].Value.Split(", ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    Match om = opSubject.Match(op);
                    if (!om.Success) continue;
                    string subject = om.Groups["subject"].Value;
                    if (!result.TryGetValue(subject, out HashSet<string>? set)) result[subject] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(cls);
                }
            }
        }

        return result;
    }

    // ---- Frontend: three independent agents instead of one call for the whole page. Each group is only
    // sent to the AI when it actually has grounded facts — a group with nothing is skipped entirely (no
    // AI call, no manufactured "nothing here" section), rather than relying on prompt discipline alone to
    // keep a mostly-empty topic from being padded into a paragraph. See DocumentationProjection.FrontendPage.

    private (string Markdown, bool HasContent) FrontendRolesAndNav(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        var roles = Nodes(s, "Role").ToList();
        var routes = Nodes(s, "Route").ToList();
        bool hasContent = roles.Count > 0 || routes.Count > 0;
        if (!hasContent) return ("", false);

        if (roles.Count > 0)
        {
            sb.AppendLine("## Roles\n");
            foreach (KnowledgeNode r in roles.OrderBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(r, "name")}** (`{Prop(r, "value")}`)");
        }

        if (routes.Count > 0)
        {
            sb.AppendLine("\n## Routes\n");
            foreach (KnowledgeNode r in routes.OrderBy(n => Prop(n, "path")))
            {
                string type = Prop(r, "type") is { Length: > 0 } t ? $" ({t})" : "";
                string guard = Prop(r, "protected") == "yes" ? " · protected" : "";
                string routeRoles = Prop(r, "roles") is { Length: > 0 } rr ? $" · roles: {rr}" : "";
                sb.AppendLine($"- `{Prop(r, "path")}`{type}{guard}{routeRoles}");
            }
        }

        if (roles.Count > 0 && routes.Count > 0)
        {
            Dictionary<string, List<string>> access = RoleBackendAccess(s, roles, routes);
            if (access.Count > 0)
            {
                sb.AppendLine("\n## Role → backend access (resolved)\n");
                foreach ((string role, List<string> endpoints) in access.OrderBy(kv => kv.Key))
                    sb.AppendLine($"- **{role}**: {string.Join(", ", endpoints)}");
            }
        }

        return (sb.ToString(), true);
    }

    /// <summary>
    /// Walks Role → Route(s) with that role → every component that route (transitively) renders → the
    /// backend calls each of those components make → the endpoint each call resolves to (MAPS_TO) — fully
    /// deterministic, no AI involved. A route marked protected with no specific roles is reachable by every
    /// role (any authenticated user), not just role-restricted routes.
    /// </summary>
    private static Dictionary<string, List<string>> RoleBackendAccess(Snapshot s, List<KnowledgeNode> roles, List<KnowledgeNode> routes)
    {
        var rendersFrom = s.Relationships.Where(r => r.Type.Value == "RENDERS")
            .GroupBy(r => Label(r.From)).ToDictionary(g => g.Key, g => g.Select(r => Label(r.To)).Distinct().ToList());
        var callsFrom = s.Relationships.Where(r => r.Type.Value == "CALLS")
            .GroupBy(r => Label(r.From)).ToDictionary(g => g.Key, g => g.Select(r => Label(r.To)).Distinct().ToList());
        var endpointOf = s.Relationships.Where(r => r.Type.Value == "MAPS_TO")
            .GroupBy(r => Label(r.From)).ToDictionary(g => g.Key, g => g.Select(r => Label(r.To)).Distinct().ToList());

        List<string> ReachableEndpoints(IEnumerable<string> startRoutes)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<string>(startRoutes);
            var endpoints = new HashSet<string>();
            while (queue.Count > 0)
            {
                string node = queue.Dequeue();
                if (!visited.Add(node)) continue;
                if (endpointOf.TryGetValue(node, out List<string>? eps)) foreach (string e in eps) endpoints.Add(e);
                if (callsFrom.TryGetValue(node, out List<string>? calls)) foreach (string c in calls) queue.Enqueue(c);
                if (rendersFrom.TryGetValue(node, out List<string>? children)) foreach (string c in children) queue.Enqueue(c);
            }

            return endpoints.OrderBy(e => e).ToList();
        }

        var result = new Dictionary<string, List<string>>();
        foreach (KnowledgeNode role in roles)
        {
            string? roleName = Prop(role, "name");
            if (string.IsNullOrEmpty(roleName)) continue;

            var startRoutes = routes.Where(r =>
                (Prop(r, "roles") is { Length: > 0 } rr && rr.Split(',').Select(t => t.Trim()).Contains(roleName))
                || (Prop(r, "protected") == "yes" && string.IsNullOrEmpty(Prop(r, "roles"))))
                .Select(r => Prop(r, "path") ?? "");

            List<string> endpoints = ReachableEndpoints(startRoutes);
            if (endpoints.Count > 0) result[roleName] = endpoints;
        }

        return result;
    }

    private (string Markdown, bool HasContent) FrontendScreens(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        var grids = Nodes(s, "DataGrid").ToList();
        var filters = Nodes(s, "Filter").ToList();
        var importExport = Nodes(s, "ImportExport").ToList();
        var formFields = Nodes(s, "FormField").ToList();
        bool hasContent = grids.Count > 0 || filters.Count > 0 || importExport.Count > 0 || formFields.Count > 0;
        if (!hasContent) return ("", false);

        if (grids.Count > 0)
        {
            sb.AppendLine("## Data grids\n");
            foreach (KnowledgeNode g in grids.OrderBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(g, "name")}** — columns: {Prop(g, "columns")}");
        }

        if (filters.Count > 0)
        {
            sb.AppendLine("\n## Filters\n");
            foreach (IGrouping<string, KnowledgeNode> group in filters.GroupBy(f => Prop(f, "component") ?? "").OrderBy(g => g.Key))
                sb.AppendLine($"- **{group.Key}**: {string.Join(", ", group.Select(f => Prop(f, "name")).OrderBy(n => n))}");
        }

        if (importExport.Count > 0)
        {
            sb.AppendLine("\n## Import / export\n");
            foreach (KnowledgeNode ie in importExport.OrderBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(ie, "name")}** ({Prop(ie, "kind")})");
        }

        if (formFields.Count > 0)
        {
            sb.AppendLine("\n## Forms\n");
            foreach (IGrouping<string, KnowledgeNode> group in formFields.GroupBy(f => Prop(f, "form") ?? "").OrderBy(g => g.Key))
            {
                sb.AppendLine($"### {group.Key}\n");
                foreach (KnowledgeNode f in group.OrderBy(n => Prop(n, "name")))
                {
                    string validation = Prop(f, "validation") is { Length: > 0 } v ? $" — _{v}_" : "";
                    sb.AppendLine($"- **{Prop(f, "name")}**{validation}");
                }
                sb.AppendLine();
            }
        }

        return (sb.ToString(), true);
    }

    private (string Markdown, bool HasContent) FrontendComponents(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        var components = Nodes(s, "UIComponent").ToList();
        var pages = components.Where(c => Prop(c, "kind") == "page").ToList();
        var comps = components.Where(c => Prop(c, "kind") is null or "component" or "layout").ToList();
        var hooks = Nodes(s, "Hook").ToList();
        var contexts = Nodes(s, "Context").ToList();
        var uiServices = Nodes(s, "UIService").ToList();
        var guards = Nodes(s, "Guard").Concat(Nodes(s, "Interceptor")).ToList();
        var maps = s.Relationships.Where(r => r.Type.Value == "MAPS_TO").ToList();
        bool hasContent = pages.Count > 0 || comps.Count > 0 || hooks.Count > 0 || contexts.Count > 0
            || uiServices.Count > 0 || guards.Count > 0 || maps.Count > 0;
        if (!hasContent) return ("", false);

        // What each page/component actually renders — grounded via React's own PascalCase-JSX-tag
        // convention (ReactCompositionAnalyzer), not tied to any particular UI library.
        var renders = s.Relationships.Where(r => r.Type.Value == "RENDERS")
            .GroupBy(r => Label(r.From)).ToDictionary(g => g.Key, g => g.Select(r => Label(r.To)).Distinct().OrderBy(n => n).ToList());
        string Composes(KnowledgeNode n) =>
            renders.TryGetValue(Prop(n, "name") ?? "", out List<string>? children) && children.Count > 0
                ? $" · renders: {string.Join(", ", children)}" : "";

        if (pages.Count > 0)
        {
            sb.AppendLine("## Pages\n");
            foreach (KnowledgeNode p in pages.OrderBy(c => Prop(c, "name")))
                sb.AppendLine($"- **{Prop(p, "name")}**{Rendering(p)}{HooksOf(p)}{Composes(p)}");
            sb.AppendLine();
        }

        if (comps.Count > 0)
        {
            sb.AppendLine("## Components\n");
            foreach (KnowledgeNode c in comps.OrderBy(n => Prop(n, "name")))
            {
                string selector = Prop(c, "selector") is { Length: > 0 } x ? $" (`{x}`)" : "";
                sb.AppendLine($"- **{Prop(c, "name")}**{selector}{Rendering(c)}{PropsOf(c)}{HooksOf(c)}{Composes(c)}");
            }
        }

        if (hooks.Count > 0)
        {
            sb.AppendLine("\n## Custom hooks\n");
            foreach (KnowledgeNode h in hooks.OrderBy(n => Prop(n, "name"))) sb.AppendLine($"- **{Prop(h, "name")}**");
        }

        if (contexts.Count > 0)
        {
            sb.AppendLine("\n## State / context\n");
            foreach (KnowledgeNode c in contexts.OrderBy(n => Prop(n, "name"))) sb.AppendLine($"- **{Prop(c, "name")}**");
        }

        if (uiServices.Count > 0)
        {
            sb.AppendLine("\n## Services\n");
            foreach (KnowledgeNode svc in uiServices.OrderBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(svc, "name")}**");
        }

        if (guards.Count > 0)
        {
            sb.AppendLine("\n## Guards & interceptors\n");
            foreach (KnowledgeNode g in guards.OrderBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(g, "name")}** ({g.Kind.Value})");
        }

        if (maps.Count > 0)
        {
            sb.AppendLine("\n## Backend calls (resolved)\n");
            foreach (Relationship m in maps) sb.AppendLine($"- {Label(m.From)} → {Label(m.To)}");
        }

        return (sb.ToString(), true);
    }

    private bool HasSecurity(Snapshot s) =>
        Nodes(s, "Technology").Any(n => Prop(n, "category") is "Authentication" or "Security")
        || s.Nodes.Any(n => Prop(n, "authorize") is { } a && a != "AllowAnonymous")
        || Nodes(s, "AuthScheme").Any() || Nodes(s, "AuthProvider").Any()
        || Nodes(s, "TokenStorage").Any() || Nodes(s, "TokenAttachment").Any();

    private string Security(Snapshot s, string app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {app} — Security & Authentication\n");

        sb.AppendLine("## Authentication\n");
        var auth = Nodes(s, "Technology").Where(n => Prop(n, "category") == "Authentication").DistinctBy(n => Prop(n, "name")).ToList();
        var authSchemes = Nodes(s, "AuthScheme").DistinctBy(n => Prop(n, "name")).ToList();
        if (auth.Count > 0) foreach (KnowledgeNode t in auth) sb.AppendLine($"- **{Prop(t, "name")}** — `{Prop(t, "package")}`");
        if (authSchemes.Count > 0)
            foreach (KnowledgeNode t in authSchemes)
            {
                string arg = Prop(t, "arg") is { Length: > 0 } a ? $" (scheme: `{a}`)" : "";
                sb.AppendLine($"- **{Prop(t, "name")}** authentication scheme is registered in the request pipeline{arg}.");
            }
        if (auth.Count == 0 && authSchemes.Count == 0) sb.AppendLine("_No authentication mechanism was detected in the model._");

        var middleware = Nodes(s, "Middleware").DistinctBy(n => Prop(n, "name")).OrderBy(n => int.TryParse(Prop(n, "order"), out int o) ? o : int.MaxValue).ToList();
        var authzPolicies = Nodes(s, "Authorization").ToList();
        var authFilters = Nodes(s, "Filter").Where(n => Prop(n, "kind") == "authorization").ToList();
        var otherFilters = Nodes(s, "Filter").Where(n => Prop(n, "kind") != "authorization").ToList();
        if (middleware.Count > 0 || authzPolicies.Count > 0 || authFilters.Count > 0)
        {
            sb.AppendLine("\n## Request pipeline & filters\n");
            if (middleware.Count > 0)
            {
                sb.AppendLine("Middleware pipeline stages detected:");
                foreach (KnowledgeNode m in middleware) sb.AppendLine($"- {Prop(m, "name")}");
            }
            if (authzPolicies.Count > 0) sb.AppendLine("\nAuthorization policies are registered (`AddAuthorization`) beyond simple role checks.");
            if (authFilters.Count > 0)
            {
                sb.AppendLine("\nCustom authorization filters (run before controller actions, independent of `[Authorize]` attributes):");
                foreach (KnowledgeNode f in authFilters) sb.AppendLine($"- **{Prop(f, "name")}**");
            }
            if (otherFilters.Count > 0)
            {
                sb.AppendLine("\nOther cross-cutting filters in the request pipeline:");
                foreach (KnowledgeNode f in otherFilters.OrderBy(n => Prop(n, "kind")))
                    sb.AppendLine($"- **{Prop(f, "name")}** — {Prop(f, "kind")} filter");
            }
        }

        var sec = Nodes(s, "Technology").Where(n => Prop(n, "category") == "Security").DistinctBy(n => Prop(n, "name")).ToList();
        if (sec.Count > 0)
        {
            sb.AppendLine("\n## Password & secret handling\n");
            foreach (KnowledgeNode t in sec) sb.AppendLine($"- **{Prop(t, "name")}** — `{Prop(t, "package")}`");
        }

        sb.AppendLine("\n## Authorization\n");
        var protectedCtrls = Nodes(s, "Controller").Where(n => Prop(n, "authorize") is { } a && a != "AllowAnonymous").ToList();
        var protectedEps = Nodes(s, "Endpoint").Where(n => Prop(n, "authorize") is { } a && a != "AllowAnonymous").ToList();
        if (protectedCtrls.Count > 0)
        {
            sb.AppendLine("Access-controlled controllers:");
            foreach (KnowledgeNode c in protectedCtrls.OrderBy(n => Prop(n, "name")))
                sb.AppendLine($"- **{Prop(c, "name")}** — {Prop(c, "authorize")}");
        }
        else if (protectedEps.Count > 0)
        {
            sb.AppendLine("Access-controlled endpoints:");
            foreach (KnowledgeNode e in protectedEps.OrderBy(n => Prop(n, "route")).Take(40))
                sb.AppendLine($"- `{Prop(e, "verb")} {Prop(e, "route")}` — {Prop(e, "authorize")}");
        }
        else sb.AppendLine("_No `[Authorize]` restrictions were detected; endpoints appear to be anonymous._");

        // The flip side, previously never rendered: which endpoints are explicitly PUBLIC. A security page
        // that only ever lists what's protected leaves "is everything else protected too?" unanswered —
        // this makes the unprotected surface, and any likely login/token entry point within it, explicit.
        var allEndpoints = Nodes(s, "Endpoint").ToList();
        var publicEps = allEndpoints.Where(n => Prop(n, "authorize") is "AllowAnonymous" or null).ToList();
        if (allEndpoints.Count > 0)
        {
            sb.AppendLine($"\n## Public (unauthenticated) endpoints\n");
            sb.AppendLine($"{publicEps.Count} of {allEndpoints.Count} endpoint(s) require no authentication.\n");
            if (publicEps.Count > 0)
            {
                foreach (KnowledgeNode e in publicEps.OrderBy(n => Prop(n, "route")).Take(40))
                {
                    string route = Prop(e, "route") ?? "";
                    string action = Prop(e, "action") ?? "";
                    bool looksLikeAuthEntry = ContainsAny(route, "login", "signin", "sign-in", "token", "authenticate", "auth")
                        || ContainsAny(action, "login", "signin", "token", "authenticate");
                    string flag = looksLikeAuthEntry ? " — _looks like a login/token entry point_" : "";
                    sb.AppendLine($"- `{Prop(e, "verb")} {route}`{flag}");
                }
            }
        }

        var authProviders = Nodes(s, "AuthProvider").DistinctBy(n => Prop(n, "name")).ToList();
        var tokenStorage = Nodes(s, "TokenStorage").DistinctBy(n => Prop(n, "location")).ToList();
        var tokenAttachment = Nodes(s, "TokenAttachment").DistinctBy(n => Prop(n, "pattern")).ToList();
        if (authProviders.Count > 0 || tokenStorage.Count > 0 || tokenAttachment.Count > 0)
        {
            sb.AppendLine("\n## Frontend authentication\n");
            if (authProviders.Count > 0)
            {
                sb.AppendLine("Identity provider SDK(s) used by the frontend:");
                foreach (KnowledgeNode p in authProviders) sb.AppendLine($"- **{Prop(p, "name")}**");
            }
            else sb.AppendLine("_No identity-provider SDK was detected — the frontend likely authenticates directly against the backend's own login endpoint._");

            if (tokenStorage.Count > 0)
            {
                sb.AppendLine("\nWhere the frontend keeps the auth token client-side:");
                foreach (KnowledgeNode t in tokenStorage) sb.AppendLine($"- **{Prop(t, "location")}**");
            }

            if (tokenAttachment.Count > 0)
            {
                sb.AppendLine("\nHow the token is attached to outgoing API requests:");
                foreach (KnowledgeNode t in tokenAttachment) sb.AppendLine($"- **{Prop(t, "pattern")}**");
            }
        }

        return sb.ToString();
    }

    private static bool ContainsAny(string text, params string[] candidates) =>
        candidates.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));

    // Deterministic architecture-pattern detection from the shape of the Knowledge Model.
    private IEnumerable<(string Name, string Evidence)> DetectPatterns(Snapshot s)
    {
        var kinds = s.Nodes.Select(n => n.Kind.Value).ToHashSet();
        bool hasCtrl = kinds.Contains("Controller"), hasSvc = kinds.Contains("Service"), hasRepo = kinds.Contains("Repository");

        if (hasCtrl && hasSvc)
            yield return ("Layered architecture", $"Requests flow through controllers → services{(hasRepo ? " → repositories" : "")}.");
        if (hasRepo || Nodes(s, "Interface").Any(n => (Prop(n, "name") ?? "").EndsWith("Repository")))
            yield return ("Repository pattern", "Data access is encapsulated behind repository types.");
        if (s.Relationships.Any(r => r.Type.Value == "IMPLEMENTS"))
            yield return ("Dependency injection", "Implementations are bound to interfaces via DI registrations.");
        if (kinds.Contains("Command") || kinds.Contains("Query"))
            yield return ("CQRS / mediator", "Requests are modeled as commands and queries dispatched through a mediator (IRequest/IRequestHandler).");
        if (kinds.Contains("Validator"))
            yield return ("Validation pipeline", "Requests/models are validated with FluentValidation validators.");
        if (kinds.Contains("Messaging"))
            yield return ("Event-driven / messaging", "The application integrates a message bus for asynchronous communication.");
        var projects = Nodes(s, "Project").Select(n => Prop(n, "name") ?? "").ToList();
        if (projects.Count(p => p.Contains("Core") || p.Contains("Domain") || p.Contains("Application") || p.Contains("Infrastructure")) >= 2)
            yield return ("Clean architecture", "The solution is separated into Core/Application/Infrastructure projects.");
    }

    // Cross-cutting infrastructure detected from framework registrations + middleware (caching, auth, CORS, …).
    private void AppendCrossCutting(StringBuilder sb, Snapshot s)
    {
        var groups = new[]
        {
            ("AuthScheme", "Authentication"), ("Authorization", "Authorization"), ("Cors", "CORS"),
            ("Cache", "Caching"), ("DataAccess", "Data access"), ("Messaging", "Messaging & events"),
            ("BackgroundJob", "Background jobs"), ("Resilience", "Resilience"), ("HealthCheck", "Health checks"),
            ("Logging", "Logging & audit"), ("Validator", "Validation"), ("Filter", "Filters"),
        };
        var present = groups.Where(g => Nodes(s, g.Item1).Any()).ToList();
        var middleware = Nodes(s, "Middleware").OrderBy(n => int.TryParse(Prop(n, "order"), out int o) ? o : 0)
            .Select(n => Prop(n, "name")).Where(n => n is not null).Distinct().ToList();
        if (present.Count == 0 && middleware.Count == 0) return;

        sb.AppendLine("## Cross-cutting concerns\n");
        foreach ((string kind, string heading) in present)
        {
            var names = Nodes(s, kind).Select(n => Prop(n, "name")).Where(n => n is not null).Distinct().ToList();
            sb.AppendLine($"- **{heading}:** {string.Join(", ", names)}");
        }
        if (middleware.Count > 0)
            sb.AppendLine($"- **Request pipeline:** {string.Join(" → ", middleware)}");
        sb.AppendLine();
    }

    // The messaging architecture: broker(s), who publishes what, who consumes what, and DLQ — broker-agnostic.
    private void AppendMessaging(StringBuilder sb, Snapshot s)
    {
        var brokers = Nodes(s, "MessageBroker").Select(n => Prop(n, "name")).Where(n => n is not null).Distinct().ToList();
        var publishes = s.Relationships.Where(r => r.Type.Value == "PUBLISHES").ToList();
        var consumes = s.Relationships.Where(r => r.Type.Value == "CONSUMES").ToList();
        var consumers = Nodes(s, "Consumer").ToList();
        bool dlq = Nodes(s, "Messaging").Any(n => (Prop(n, "name") ?? "").Contains("Dead-letter"));
        if (brokers.Count == 0 && publishes.Count == 0 && consumes.Count == 0 && consumers.Count == 0) return;

        sb.AppendLine("## Messaging\n");
        if (brokers.Count > 0) sb.AppendLine($"**Transport:** {string.Join(", ", brokers)}  ");
        sb.AppendLine($"**Dead-letter queue:** {(dlq ? "configured" : "not detected")}\n");

        if (publishes.Count > 0)
        {
            sb.AppendLine("Publishers → messages:");
            foreach (Relationship r in publishes.Take(40)) sb.AppendLine($"- `{Label(r.From)}` → **{Label(r.To)}**");
            sb.AppendLine();
        }
        if (consumes.Count > 0)
        {
            sb.AppendLine("Messages → consumers:");
            foreach (Relationship r in consumes.Take(40)) sb.AppendLine($"- **{Label(r.To)}** → `{Label(r.From)}`");
            sb.AppendLine();
        }
        else if (consumers.Count > 0)
            sb.AppendLine($"Consumers: {string.Join(", ", consumers.Select(n => Prop(n, "name")).Distinct().Take(30))}\n");
    }

    // CQRS message model, when present (commands, queries, events, and their handlers).
    private void AppendCqrs(StringBuilder sb, Snapshot s)
    {
        var commands = Nodes(s, "Command").ToList();
        var queries = Nodes(s, "Query").ToList();
        var events = Nodes(s, "Event").ToList();
        if (commands.Count + queries.Count + events.Count == 0) return;

        sb.AppendLine("## CQRS\n");
        sb.AppendLine($"Requests are modeled as **{commands.Count} command(s)**, **{queries.Count} query(ies)**, and **{events.Count} event(s)**, dispatched through a mediator.\n");
        if (commands.Count > 0) sb.AppendLine($"**Commands:** {string.Join(", ", commands.Select(n => Prop(n, "name")).Take(40))}\n");
        if (queries.Count > 0) sb.AppendLine($"**Queries:** {string.Join(", ", queries.Select(n => Prop(n, "name")).Take(40))}\n");
        if (events.Count > 0) sb.AppendLine($"**Events:** {string.Join(", ", events.Select(n => Prop(n, "name")).Take(40))}\n");
    }

    // ======================= helpers =======================

    private static string RoleOf(string kind) => kind switch
    {
        "Controller" => "Receives HTTP requests and coordinates responses.",
        "Endpoint" => "A single HTTP operation the application exposes.",
        "Service" => "Encapsulates business logic and orchestrates operations.",
        "Repository" => "Provides data access for a domain type.",
        "Interface" => "A contract implemented elsewhere in the application.",
        "DataStore" => "Persists and retrieves application data.",
        "Entity" => "A domain data type the application manages.",
        "UIComponent" => "A building block of the user interface.",
        "UIService" => "A frontend service (data access, state, or shared logic).",
        "Hook" => "A reusable piece of React state/behavior (custom hook).",
        "Context" => "Shared React state exposed to the component tree.",
        "Guard" => "Controls route access on the frontend.",
        "Interceptor" => "Intercepts outgoing HTTP requests on the frontend.",
        "Component" => "A structural building block of the application.",
        "Route" => "A navigable path in the frontend.",
        "ApiCall" => "A call the frontend makes to a backend endpoint.",
        "Configuration" => "An application configuration value.",
        "Project" => "A build unit (project) within the application.",
        "Technology" => "A framework or library the application depends on.",
        "Cache" => "A caching mechanism (in-memory or distributed).",
        "AuthScheme" => "A configured authentication scheme.",
        "Cors" => "Cross-origin resource sharing configuration.",
        "HealthCheck" => "A health-check probe or endpoint.",
        "Logging" => "A logging / audit mechanism.",
        "Messaging" => "A messaging or event-bus integration.",
        "BackgroundJob" => "A background-job or scheduling mechanism.",
        "Middleware" => "A stage in the HTTP request pipeline.",
        "Authorization" => "Authorization policy configuration.",
        "Command" => "A CQRS command (a state-changing request).",
        "Query" => "A CQRS query (a read request).",
        "Event" => "A domain event / notification.",
        "Handler" => "Handles a CQRS request or event.",
        "Validator" => "Validates a request/model (FluentValidation).",
        "MessageBroker" => "A message broker / transport (RabbitMQ, Service Bus, SQS, Kafka, …).",
        "Consumer" => "Consumes messages from the broker.",
        "Message" => "A message/event sent over the broker.",
        "DataAccess" => "A data-access approach (EF Core, Dapper, ADO.NET, MongoDB, …).",
        "Filter" => "An MVC filter (authorization / action / exception / result).",
        "Resilience" => "A resilience policy (retry, circuit-breaker).",
        _ => "A tracked element of the application.",
    };

    private IEnumerable<string> CapabilityLines(Snapshot s) =>
        Nodes(s, "Endpoint").OrderBy(n => Prop(n, "route")).Select(Operation).Distinct();

    // Phrase an endpoint as an operation. Action-routed endpoints (the route's last literal segment IS the
    // action, e.g. /Manage/MyAccount or /api/auth/login) read best as the action name; REST resource routes
    // (/api/projects/{id}) read best as a verb phrase ("Retrieve a project by id").
    private static string Operation(KnowledgeNode endpoint)
    {
        string? verb = Prop(endpoint, "verb");
        string? route = Prop(endpoint, "route");
        string? action = Prop(endpoint, "action");

        if (action is { Length: > 0 })
        {
            string last = Segments(route).LastOrDefault(IsLiteral) ?? "";
            if (Squash(Humanize(action)) == Squash(Humanize(last))) return Humanize(action);
        }

        return UseCase(verb, route);
    }

    private static IEnumerable<string> Segments(string? route) =>
        (route ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
    private static bool IsLiteral(string seg) => seg.Length > 0 && seg[0] is not ('{' or '[' or ':' or '$');
    private static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // "GET /api/projects/{id}" -> "Retrieve a project by id". The resource is the first real path segment
    // (the controller's resource), not the last; route tokens ({id}, [action]) and 'api' are not nouns.
    private static string UseCase(string? verb, string? route)
    {
        string plural = Segments(route)
            .FirstOrDefault(seg => IsLiteral(seg) && !string.Equals(seg, "api", StringComparison.OrdinalIgnoreCase))
            ?? "resource";
        plural = Humanize(plural).ToLowerInvariant();
        string singular = Singular(plural);
        bool byId = (route ?? "").Contains('{') || (route ?? "").Contains(':') || (route ?? "").Contains('$');

        return (verb ?? "").ToUpperInvariant() switch
        {
            "GET" => byId ? $"Retrieve a {singular} by id" : $"Retrieve all {plural}",
            "POST" => $"Create a {singular}",
            "PUT" => $"Update a {singular}",
            "PATCH" => $"Modify a {singular}",
            "DELETE" => $"Delete a {singular}",
            _ => $"Operate on {plural}",
        };
    }

    private string? OwnerOf(Snapshot s, KnowledgeNode endpoint)
    {
        Relationship? exposes = s.Relationships.FirstOrDefault(r => r.Type.Value == "EXPOSES" && r.To.Equals(endpoint.Identity));
        if (exposes is null) return null;
        KnowledgeNode? owner = s.FindNode(exposes.From);

        return owner is null ? Label(exposes.From) : (Prop(owner, "name") ?? Label(owner.Identity));
    }

    private static string Singular(string noun) =>
        noun.EndsWith("ies", StringComparison.OrdinalIgnoreCase) ? noun[..^3] + "y" :
        noun.EndsWith('s') && noun.Length > 1 ? noun[..^1] : noun;

    private static string StripSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? s[..^suffix.Length] : s;

    // "CustomerController" -> "Customer"; "customer-orders" -> "Customer orders".
    private static string Humanize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '-' or '_' or '.') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }

        return sb.ToString();
    }

    private static string Sentence(bool include, string text) => include ? text : "";

    private static string Rendering(KnowledgeNode c) => Prop(c, "rendering") is { Length: > 0 } r ? $" — _{r}_" : "";
    private static string PropsOf(KnowledgeNode c) => Prop(c, "props") is { Length: > 0 } p ? $" · props: {p}" : "";
    private static string HooksOf(KnowledgeNode c) => Prop(c, "hooks") is { Length: > 0 } h ? $" · hooks: {h}" : "";

    private static IEnumerable<KnowledgeNode> Nodes(Snapshot s, string kind) => s.Nodes.Where(n => n.Kind.Value == kind);
    private static string? Prop(KnowledgeNode n, string key) => n.Prop(key);
    private static string Label(KnowledgeIdentity id) => id.ShortName;
    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
