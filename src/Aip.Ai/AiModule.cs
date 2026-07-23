using System.Collections.Concurrent;
using System.Text.Json;

using Aip.Abstractions.Ai;
using Aip.Core.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Ai;

/// <summary>Token accounting — attributable per scope, aggregated into one ledger.</summary>
internal sealed class TokenAccountant : ITokenAccountant
{
    private readonly ConcurrentDictionary<string, AiUsage> _byScope = new();

    public void Record(string scope, AiUsage usage) =>
        _byScope.AddOrUpdate(scope, usage,
            (_, e) => new AiUsage(e.PromptTokens + usage.PromptTokens, e.CompletionTokens + usage.CompletionTokens));

    public AiUsage Total
    {
        get
        {
            int p = 0, c = 0;
            foreach (AiUsage u in _byScope.Values) { p += u.PromptTokens; c += u.CompletionTokens; }

            return new AiUsage(p, c);
        }
    }

    public IReadOnlyDictionary<string, AiUsage> ByScope => _byScope;
}

/// <summary>Grounding: serializes a projection view model into the only text the AI may see.</summary>
internal sealed class ContextBuilder : IContextBuilder
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Build(object viewModel) => JsonSerializer.Serialize(viewModel, Options);
}

/// <summary>Governed prompt templates (versioned). The AI never sees source — only these + view models.</summary>
internal static class PromptTemplates
{
    public const string System =
        "You are a senior technical writer producing engineering documentation. You are given a page of " +
        "GROUNDED MARKDOWN derived from a structured knowledge model. Rewrite it into clear, descriptive " +
        "prose and Markdown. You may add explanation, structure, and headings, but use ONLY the facts " +
        "present — never invent endpoints, technologies, entities, or components. Preserve every Markdown " +
        "table and every ```mermaid``` code block verbatim — copy it character-for-character, including " +
        "the literal `-->`/`<`/`>` arrow and comparison characters exactly as given. Do NOT HTML-entity-" +
        "escape anything inside a mermaid block (never turn `-->` into `--&gt;`, or `<`/`>` into `&lt;`/" +
        "`&gt;` anywhere in it) — mermaid's own parser requires the literal characters, and an escaped " +
        "arrow breaks the diagram entirely. If information is absent, say so plainly.\n\n" +
        "Do NOT transcribe the grounded bullet lists back out as bullet lists with the same facts in the " +
        "same order — that is not documentation, it's a copy. SYNTHESIZE: group related facts by what a " +
        "reader actually wants to know (by role, by workflow, by capability) and explain them as plain " +
        "sentences. For example, given ten routes each individually marked with which role can reach them, " +
        "do not list all ten with their restriction repeated on each line — instead say what each ROLE can " +
        "do as a whole (\"Managers can access the Contracts and Obligations areas; administrative pages " +
        "such as the dashboard, settings, and user management are limited to Admins\"). Only cite an " +
        "individual fact (a specific path, field name, column) when it adds real information a summary " +
        "would lose — never as a mechanical enumeration of everything in the source. If you catch yourself " +
        "writing one bullet per source fact, stop and rewrite it as a paragraph instead.\n\n" +
        "Do NOT pad a section with generic scaffolding narrative when the grounded facts for it are thin. " +
        "Sentences like 'the user enters through the root route, where the shared layout and page content " +
        "are assembled' describe how routing frameworks work in general, not anything specific this " +
        "application does — they read as insight but carry no information, because the same sentence would " +
        "be true of nearly any app using that framework. If a section's grounded facts don't support a real " +
        "paragraph, write one or two honest, direct sentences saying what's missing and stop — do not " +
        "restate the same absence three ways to reach paragraph length. A short accurate section beats a " +
        "long vague one every time.\n\n" +
        "The 'never invent' rule above applies with special force to PROPER NOUNS AND SPECIFIC NAMES: a " +
        "controller name, component name, table name, column name, screen name, or endpoint path may only " +
        "appear in your output if that exact string is present in the grounded source below. If you want to " +
        "describe a capability but the source doesn't name the specific class, screen, or field responsible " +
        "for it, describe the capability in general terms instead — never invent a plausible-sounding name " +
        "to attach it to, and never invent a UI element (a table, a column set, a sub-screen) that isn't " +
        "explicitly described in the grounded facts just because it would make the writing feel more " +
        "complete. When several individually-named facts don't obviously add up to a feature you'd like to " +
        "describe, resist the pull to invent the missing piece connecting them — state what's grounded and " +
        "stop there.";

    // Shared by the three product-spec templates below (the only ones that ever receive {{notes}} — see
    // DocumentationProjection.Page). {{notes}} is always present in values but resolves to an empty string
    // when nothing was found, so this instruction is inert (nothing to cross-reference) on a page with no
    // notes rather than needing its own conditional wording. Deliberately does NOT ask the AI to reproduce
    // any of the notes text itself (verbatim or otherwise) — a long README pushed the AI's own response
    // past its output length limit mid-generation when it was asked to transcribe "exactly as it reads",
    // and even short of that failure mode, an LLM asked to reproduce text verbatim commonly paraphrases it
    // anyway. The raw notes are instead appended as their own deterministic section by DocumentationProjection
    // after this call returns — the AI's only job here is deciding what's corroborated.
    private const string NotesInstruction =
        "Below the grounded model you may also see project notes (a README or CLAUDE.md) — these are " +
        "human-authored and may be stale, incomplete, or simply wrong, unlike the grounded model above. " +
        "Cross-reference each claim in the notes against the grounded facts: where a claim is corroborated " +
        "by something in the model, weave it into the relevant section naturally, in your own words. Where " +
        "you cannot find support for a claim in the grounded facts, do not state it as fact and do not " +
        "mention it at all — the raw notes are shown separately elsewhere on the page for reference, so " +
        "your job is only to enrich the sections above with what you can actually corroborate. Never quote " +
        "or reproduce the notes text itself. If no notes are present below, ignore this instruction " +
        "entirely and do not mention their absence.";

    // The value passed as {{model}} is the deterministic page for this document; the AI rewrites it into
    // richer prose. Keys match the template names the DocumentationProjection asks for.
    private static readonly Dictionary<string, string> Templates = new()
    {
        // Product pages stay at business level, deliberately opposite depth from the tech-* pages below —
        // no class names, method names, endpoint routes, or entity/table names anywhere. A reader who has
        // never seen the code should be able to read these and understand what the product IS and WHY it
        // matters; if a sentence would only make sense to someone reading the source, it belongs on a
        // technical page instead, not here.
        ["product-overview"] =
            "Rewrite this into a product overview for '{{app}}' with sections '## What is {{app}}?', " +
            "'## The problem it solves', '## Who it's for', and '## Key value'. Business language only — no " +
            "class names, method names, routes, or entity/table names; describe capabilities the way a " +
            "non-technical stakeholder would, not the code that implements them. " + NotesInstruction +
            " Grounded source:\n{{model}}{{notes}}",
        ["product-features"] =
            "Rewrite this into a features page for '{{app}}', describing each capability in plain language " +
            "grouped by area. Business language only — no class names, method names, routes, or entity/table " +
            "names; describe what a user can DO, not which code does it. " + NotesInstruction +
            " Grounded source:\n{{model}}{{notes}}",
        ["product-use-cases"] =
            "Rewrite this into a use-cases page for '{{app}}', each a short scenario of who does what and why. " +
            "Business language only — no class names, method names, routes, or entity/table names. " + NotesInstruction +
            " Grounded source:\n{{model}}{{notes}}",
        ["tech-architecture"] =
            "Rewrite this architecture page for '{{app}}' with descriptive prose on layers, components, and how " +
            "they fit together. Keep every mermaid diagram (there may be more than one — e.g. a component-" +
            "relationship graph and a request-flow sequence diagram) and every table exactly as-is, each in its " +
            "own section, none merged together or dropped. The 'Components' section and the diagrams list every " +
            "controller/service/entity actually found — when describing what handles a particular concern (e.g. " +
            "'which controller manages X'), name only a controller that is explicitly listed there; if no listed " +
            "controller obviously matches, describe the concern without naming a specific controller for it " +
            "rather than guessing one into existence. If a 'Status workflows' section is present, keep it as " +
            "its own section listing the named states per property — do not invent a transition order between " +
            "the states beyond what's stated (no order is grounded). Grounded source:\n{{model}}",
        ["tech-api"] =
            "Rewrite this API reference for '{{app}}'. Keep every table exactly as-is; add only brief framing. " +
            "Grounded source:\n{{model}}",
        ["tech-stack"] =
            "Rewrite this into a technology-stack page for '{{app}}'. For each technology, explain what it is used " +
            "for IN THIS application, grounded in the 'used by' details (the classes and operations shown). Do NOT " +
            "write generic textbook descriptions of the package; if no usage detail is given, state only the package " +
            "and its category and say the specific usage was not detected.\n\n" +
            "Where an operation is shown with a parenthesized type — e.g. 'Add(Contract)', 'FindAsync(Contract)', " +
            "'SendAsync(EmailMessage)' — that parenthesized name is the real domain type the call operates on, " +
            "resolved from the actual code, not a guess. Use it to say what the operation actually does, not just " +
            "that it happened: 'ContractService queries, adds, and updates Contract records' rather than " +
            "'ContractService uses Add, FindAsync, and Include'. Never invent a type that wasn't given this way — " +
            "if an operation has no parenthesized type, describe it by the operation alone, don't guess what it " +
            "might affect. This is what turns a method-name list into a real explanation of what the application " +
            "does with each technology. Keep any table. Grounded source:\n{{model}}",
        ["tech-data"] =
            "Rewrite this into a data & storage page for '{{app}}'. Open with what the 'Storage landscape' facts " +
            "actually say — what kind of database/storage technology is in play (relational vs. document/NoSQL vs. " +
            "object storage), not just a bare product name. Keep the mermaid ER diagram exactly as-is if present — " +
            "it gives the reader the shape of the model before the detail. For each entity, do NOT just restate its " +
            "field table and relationship list as prose — say what it's FOR: when an entity's write-up includes a " +
            "'Used by' line, that's real, grounded proof of which service(s) create/query/update it, so use it to " +
            "explain the entity's role (e.g. 'Contract is the central record ContractService manages through its " +
            "lifecycle — created and updated there, summarized on the dashboard by DashboardService' — not just " +
            "'Contract references Macro'). If an entity has no 'Used by' line, don't invent one; describe it from " +
            "its fields and relationships alone. Keep every table. Grounded source:\n{{model}}",
        // The frontend page is split into three focused agent calls (DocumentationProjection.FrontendPage) —
        // each one is only invoked when its own topic has real grounded facts, so a template never has to
        // cope with "nothing to say" on its own. Each prompt stays narrow to its one concern instead of
        // juggling six topics in one completion.
        ["tech-frontend-roles"] =
            "Rewrite this into a short 'Roles & Navigation' section for '{{app}}'s frontend. Write it as " +
            "flowing prose, not a restatement of the grounded bullet list.\n\n" +
            "Roles & access: describe access BY ROLE, not by route. One short paragraph per role covering what " +
            "areas/workflows that role can reach and what it's blocked from. Example of the transformation " +
            "required — grounded source has 'Route /dashboard: protected, roles: ADMIN' and nine similar lines; " +
            "the page must NOT list all ten routes with their restriction. Instead write something like: " +
            "'Admins have access to the full application, including administrative areas such as the dashboard, " +
            "settings, audit log, and user management. Managers are limited to day-to-day operational areas — " +
            "contracts and obligations — and cannot reach administrative pages.' Only name an individual route " +
            "when it's genuinely notable (e.g. the login/unauthorized flow), never as an exhaustive list. If no " +
            "Roles section is present in the grounded source, do not invent a 'no role model' discussion — " +
            "describe access purely from what the Routes say (protected vs not) in one or two sentences.\n\n" +
            "Navigation flow: describe it as a short journey (where a user lands, what they typically do next), " +
            "not a route-by-route restatement.\n\n" +
            "If a 'Role → backend access (resolved)' section is present, that's a fully deterministic, " +
            "computed fact (not a guess) — it traces which backend endpoints each role can actually reach " +
            "through the pages/components that role's routes render. Weave it into the same per-role " +
            "paragraphs as real, concrete evidence (e.g. 'Admins can reach the full set of contract and user " +
            "management endpoints, while Managers are limited to...'), not as a separate disconnected list. " +
            "If a 'User journeys (by role)' section with a ```mermaid``` ```journey``` block is present, that " +
            "diagram is the exception to 'rewrite as prose' — copy it verbatim (heading, caption, and code " +
            "block unchanged) and place it after your per-role paragraphs; do not describe its content in " +
            "prose instead of keeping the diagram, and do not drop it. Grounded source:\n{{model}}",
        ["tech-frontend-screens"] =
            "Rewrite this into a short 'Screens & Capabilities' section for '{{app}}'s frontend, describing " +
            "what a user can actually do on each screen — as flowing prose, not a restatement of the grounded " +
            "bullet list. For each data grid, say what information it actually displays (name the columns, not " +
            "just 'this grid has columns') — but ONLY the columns explicitly listed in the grounded source; " +
            "if a screen is named but no grid/column/field detail was grounded for it, say only what the source " +
            "gives you (its name, or its role if that's grounded) and stop — do not invent plausible columns, " +
            "a details/history sub-table, or additional screens to round it out. For filters, say what they let " +
            "a user narrow down, grouped by screen. For import/export or file upload capability, say what it's " +
            "for. For each form, the fields it collects and their validation rules in plain language (e.g. 'X " +
            "is required'). Grounded source:\n{{model}}",
        ["tech-frontend-components"] =
            "Rewrite this into a short 'Component Inventory' section for '{{app}}'s frontend — a technical " +
            "catalogue of pages, components, hooks, state, services, and backend connectivity, as flowing " +
            "prose, not a restatement of the grounded bullet list. When a Page or Component lists what it " +
            "'renders', use that to describe what's actually built into that screen (e.g. which charts or cards " +
            "compose it) instead of describing it by name alone — but only what's listed there; do not credit a " +
            "page with functionality (e.g. that a list is editable, or saves changes somewhere) that isn't " +
            "shown in its grounded facts, even if that would be the natural feature for a page with that name " +
            "to have. Describe how frontend calls connect to backend endpoints where that's grounded. Grounded " +
            "source:\n{{model}}",
        ["tech-security"] =
            "Rewrite this into a security & authentication page for '{{app}}', as flowing prose organized by " +
            "topic, not a restatement of the grounded bullet list. If the grounded source has a '## " +
            "Vulnerabilities' heading, keep it as its own separate section, in your output, positioned first " +
            "— before authentication/authorization/anything else. These are actionable findings (a leaked " +
            "credential, an open CORS policy, a vulnerable dependency), not general security architecture " +
            "description, and folding them into another topic (e.g. merging a leaked-credential finding into " +
            "'password/secret handling' below) would bury something a reader needs to act on. You may rewrite " +
            "its wording for clarity, but keep every individual finding (the fact, the file, the severity) — " +
            "never drop one, and never merge it into a different section. Cover, wherever the grounded facts " +
            "support it: (1) how a user authenticates end-to-end — identify the actual mechanism (JWT, cookie session, " +
            "an external identity provider such as Azure AD/Okta/Auth0) and name the concrete login/token " +
            "endpoint(s) if any were found. State the endpoints as facts first; only after that, if their names " +
            "together clearly match a well-known pattern (e.g. separate login/callback/refresh-token endpoints " +
            "matching an OAuth2/OIDC authorization-code flow), you may add one soft, clearly-hedged sentence " +
            "naming that pattern ('this combination looks like...', 'this is likely...') — never state it as a " +
            "confirmed fact, and skip it entirely rather than force a guess when the endpoints don't clearly fit " +
            "a recognizable shape; (2) authorization — don't just list which controllers/endpoints are " +
            "protected, say what fraction of the API surface is protected vs. public, call out any public " +
            "endpoint that looks like a login/token entry point, and explain what the specific Roles/Policy " +
            "values found in the model actually gate (e.g. 'Admin-only endpoints include...'), synthesized by " +
            "role rather than transcribed name-by-name; (3) the request pipeline — which middleware stages and " +
            "custom authorization filters run, and what that buys the app (e.g. defense-in-depth beyond " +
            "attribute-level checks); (4) how the frontend fits in — which identity-provider SDK it uses (or " +
            "that it talks to the backend's own login endpoint directly if none was found), where it keeps the " +
            "token client-side, and how that token is attached to outgoing requests, and whether that storage " +
            "choice has any notable security implication (e.g. localStorage is readable by any script on the " +
            "page, a cookie can be marked httpOnly) — state this as a neutral technical observation only if the " +
            "grounded facts support it, never invent a finding; (5) password/secret handling, if present. Skip " +
            "any of these topics entirely when the grounded source has no facts for it — do not pad with " +
            "generic security advice. Grounded source:\n{{model}}",
        ["version-changelog"] =
            "Write a short changelog entry summarizing what changed in '{{app}}' since the previous " +
            "documented version. Use ONLY the facts given below — never invent an endpoint, entity, " +
            "relationship, or capability not listed. Group related additions/removals together and describe " +
            "their practical effect (e.g. 'Added a refund endpoint and the Refund entity that backs it' " +
            "rather than listing the endpoint and the entity as two disconnected facts) — do not just " +
            "transcribe the grounded list back out unchanged. Keep it to a few sentences or a short bulleted " +
            "list; this is a changelog, not a full page. If the changes are purely structural with no clear " +
            "practical framing (e.g. an internal rename), say so plainly rather than inventing a narrative. " +
            "If the facts below are broken out per sub-application (## headings) and/or include an " +
            "'Integrations' section, '{{app}}' is a composite application made of those sub-applications — " +
            "preserve that structure rather than flattening it back into one undifferentiated list, describe " +
            "how each sub-application's change affects '{{app}}' as a whole (not just that sub-application in " +
            "isolation), and call out any new or removed integration between sub-applications explicitly, " +
            "since that is the most important fact at this scale. Grounded source:\n{{model}}",
    };

    public static string Get(string name) => Templates.TryGetValue(name, out string? t) ? t : "{{model}}";

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        string result = template;
        foreach (KeyValuePair<string, string> v in values)
            result = result.Replace("{{" + v.Key + "}}", v.Value);

        return result;
    }
}

/// <summary>
/// The AI Platform boundary: prompt orchestration + grounding + token accounting + execution history.
/// Consumes projection view models only; never touches repositories.
/// </summary>
internal sealed class AiPlatform : IAiPlatform, IAiExecutionHistory
{
    private readonly IAiProvider _provider;
    private readonly ITokenAccountant _tokens;
    private readonly List<AiExecution> _history = new();
    private readonly object _gate = new();

    public AiPlatform(IAiProvider provider, ITokenAccountant tokens)
    {
        _provider = provider;
        _tokens = tokens;
    }

    // Aip.Ai can't reference NoOpAiProvider's concrete type (it lives in Aip.Infrastructure, which
    // depends inward on Aip.Ai, not the other way around) — a type-name string is the only check
    // available without inverting that dependency.
    public bool IsAvailable => _provider.GetType().Name != "NoOpAiProvider";

    public IReadOnlyList<AiExecution> Records { get { lock (_gate) return _history.ToList(); } }

    public async Task<string> RenderAsync(string templateName, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        string user = PromptTemplates.Render(PromptTemplates.Get(templateName), values);
        string output = await _provider.CompleteAsync(PromptTemplates.System, user, ct);

        // Prefer real usage reported by the provider; fall back to an estimate only if unavailable.
        AiUsage usage = (_provider as IAiUsageReporter)?.LastUsage
            ?? new AiUsage(Estimate(PromptTemplates.System) + Estimate(user), Estimate(output));
        _tokens.Record($"projection:{templateName}", usage);
        lock (_gate) _history.Add(new AiExecution(templateName, _provider.GetType().Name, usage, DateTimeOffset.UtcNow));

        return output;
    }

    private static int Estimate(string text) => Math.Max(1, text.Length / 4);
}

public static class AiModule
{
    public static IServiceCollection AddAipAi(this IServiceCollection services)
    {
        services.AddSingleton<ITokenAccountant, TokenAccountant>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<AiPlatform>();
        services.AddSingleton<IAiPlatform>(sp => sp.GetRequiredService<AiPlatform>());
        services.AddSingleton<IAiExecutionHistory>(sp => sp.GetRequiredService<AiPlatform>());

        return services;
    }
}
