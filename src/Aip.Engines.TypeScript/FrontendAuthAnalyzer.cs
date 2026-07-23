using System.Text.RegularExpressions;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;

using static Aip.Core.Domain.IdentitySegment;
using static Aip.Core.Domain.PropertyBag;
using static Aip.Engines.TypeScript.TsFile;

namespace Aip.Engines.TypeScript;

/// <summary>
/// Detects frontend authentication mechanics that the backend-side analyzers can't see: which
/// identity-provider SDK the app imports, where the auth token is kept client-side, and how it's attached to
/// outgoing requests. Lives in the shared TypeScript engine (not any one framework plugin) and matches on
/// <c>import</c> statements and common storage/HTTP-client APIs — the same signals apply whether the app is
/// React, Angular, or Next.js, so every frontend plugin gets this for free by referencing this engine.
/// </summary>
public sealed class FrontendAuthAnalyzer : IAnalyzer
{
    public string Name => "frontend-auth";

    private static readonly (string Pattern, string Name)[] IdentityProviders =
    {
        (@"@azure/msal-(react|browser|angular)", "Microsoft Entra ID (MSAL)"),
        (@"@okta/okta-(react|angular|vue|auth-js)", "Okta"),
        (@"@auth0/auth0-(react|angular|spa-js)", "Auth0"),
        (@"next-auth", "NextAuth.js"),
        (@"firebase/auth|@angular/fire", "Firebase Authentication"),
        (@"angular-oauth2-oidc", "OAuth2 / OpenID Connect (angular-oauth2-oidc)"),
        (@"keycloak-(angular|js)", "Keycloak"),
        (@"amazon-cognito-identity-js|aws-amplify", "Amazon Cognito"),
    };

    private static readonly Regex ImportFrom = new(@"from\s+[""']([^""']+)[""']", RegexOptions.Compiled);

    // Operation (setItem/getItem) is now its own capture group, not folded into a non-capturing alternation
    // — distinguishing reads from writes per key is what lets a downstream reader tell "this app manages
    // its own token" apart from "this app only ever reads a token something else must have written" (see
    // the aggregation in AnalyzeAsync below).
    private static readonly Regex LocalStorageToken =
        new(@"localStorage\s*\.\s*(setItem|getItem)\s*\(\s*[`'""]([^`'""]*(?:token|jwt|auth)[^`'""]*)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SessionStorageToken =
        new(@"sessionStorage\s*\.\s*(setItem|getItem)\s*\(\s*[`'""]([^`'""]*(?:token|jwt|auth)[^`'""]*)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CookieToken =
        new(@"(?:document\.cookie\s*=|Cookies\s*\.\s*set|CookieService\b(?:[^;]*)\.\s*set)[^;\n]{0,80}(?:token|jwt|auth)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Vite's `base` option, e.g. `defineConfig({ base: "/cms-ui/" })` — a genuine sub-path (not "/" or
    // empty) means this app is built to be served from underneath some other path, not standalone at a
    // domain root. That's the strongest syntactic signal available that an app is deployed as a
    // micro-frontend rather than its own standalone site. Scoped to files that look like a Vite config by
    // name, not matched anywhere in the codebase — a `base` key is a common enough object-literal property
    // name elsewhere that matching it unscoped would produce real false positives.
    private static readonly Regex ViteBasePath = new(@"base\s*:\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled);

    // Covers both the object-literal/colon form (`Authorization: \`Bearer ${t}\``) and the comma-separated
    // method-argument form Angular's HttpHeaders/HttpClient idiom uses (`.set('Authorization', 'Bearer ' + t)`,
    // `.append('Authorization', ...)`) — same fact, two syntactically different call shapes.
    private static readonly Regex AuthHeaderAttach =
        new(@"Authorization[""']?[^;\n]{0,40}Bearer", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AxiosInterceptor = new(@"axios\s*\.\s*interceptors\s*\.\s*request\s*\.\s*use", RegexOptions.Compiled);
    // Class-based (`class X implements HttpInterceptor`) and Angular 15+ functional (`HttpInterceptorFn`) forms.
    private static readonly Regex AngularInterceptorClass = new(@"implements\s+HttpInterceptor\b|:\s*HttpInterceptorFn\b", RegexOptions.Compiled);

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (TypeScriptSemanticModel)context.Model;

        var providers = new Dictionary<string, (string File, int Line)>();
        // Keyed by (location, key) rather than location alone — the whole point is telling apart two keys
        // at the same storage location that behave differently (one round-tripped by this app, one only
        // ever read). Ops accumulates every operation seen for that exact key across every file in this
        // artifact, so a key set in one file and read in another is still correctly seen as read+write.
        var storageKeys = new Dictionary<(string Location, string Key), (HashSet<string> Ops, string File, int Line)>();
        var attachPatterns = new Dictionary<string, (string File, int Line)>();
        (string Value, string Path, int Line)? viteBasePath = null;

        void RecordStorageMatch(string location, Match m, TsFile file)
        {
            string op = m.Groups[1].Value.Equals("setItem", StringComparison.OrdinalIgnoreCase) ? "set" : "get";
            string key = m.Groups[2].Value;
            var k = (location, key);
            if (!storageKeys.TryGetValue(k, out (HashSet<string> Ops, string File, int Line) entry))
                storageKeys[k] = entry = (new HashSet<string>(StringComparer.Ordinal), file.Path, LineAt(file.Text, m.Index));
            entry.Ops.Add(op);
        }

        foreach (TsFile file in model.Files)
        {
            foreach (Match m in ImportFrom.Matches(file.Text))
            {
                string src = m.Groups[1].Value;
                foreach ((string pattern, string name) in IdentityProviders)
                    if (!providers.ContainsKey(name) && Regex.IsMatch(src, pattern, RegexOptions.IgnoreCase))
                        providers[name] = (file.Path, LineAt(file.Text, m.Index));
            }

            foreach (Match m in LocalStorageToken.Matches(file.Text)) RecordStorageMatch("localStorage", m, file);
            foreach (Match m in SessionStorageToken.Matches(file.Text)) RecordStorageMatch("sessionStorage", m, file);
            if (CookieToken.Match(file.Text) is { Success: true } ck)
                storageKeys.TryAdd(("cookie", "(unnamed)"), (new HashSet<string> { "set" }, file.Path, LineAt(file.Text, ck.Index)));

            if (!attachPatterns.ContainsKey("Authorization: Bearer header") && AuthHeaderAttach.Match(file.Text) is { Success: true } ah)
                attachPatterns["Authorization: Bearer header"] = (file.Path, LineAt(file.Text, ah.Index));
            if (!attachPatterns.ContainsKey("Axios request interceptor") && AxiosInterceptor.Match(file.Text) is { Success: true } ax)
                attachPatterns["Axios request interceptor"] = (file.Path, LineAt(file.Text, ax.Index));
            if (!attachPatterns.ContainsKey("Angular HttpInterceptor") && AngularInterceptorClass.Match(file.Text) is { Success: true } ng)
                attachPatterns["Angular HttpInterceptor"] = (file.Path, LineAt(file.Text, ng.Index));

            if (viteBasePath is null && Path.GetFileName(file.Path).StartsWith("vite.config", StringComparison.OrdinalIgnoreCase)
                && ViteBasePath.Match(file.Text) is { Success: true } vb && vb.Groups[1].Value is not ("/" or ""))
                viteBasePath = (vb.Groups[1].Value, file.Path, LineAt(file.Text, vb.Index));
        }

        foreach ((string name, (string path, int line)) in providers)
        {
            Evidence ev = context.Evidence(path, line, name);
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("authprovider", name)), NodeKind.From("AuthProvider"),
                new[] { ev }, Confidence.From(0.85), Props(("name", name))));
        }

        foreach (((string location, string key), (HashSet<string> ops, string path, int line)) in storageKeys)
        {
            Evidence ev = context.Evidence(path, line, key);
            string operation = ops.Contains("set") && ops.Contains("get") ? "get+set" : ops.Contains("set") ? "set" : "get";
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("tokenstorage", $"{location}:{key}")), NodeKind.From("TokenStorage"),
                new[] { ev }, Confidence.From(0.75), Props(("location", location), ("key", key), ("operation", operation))));
        }

        foreach ((string pattern, (string path, int line)) in attachPatterns)
        {
            Evidence ev = context.Evidence(path, line, pattern);
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("tokenattachment", pattern)), NodeKind.From("TokenAttachment"),
                new[] { ev }, Confidence.From(0.75), Props(("pattern", pattern))));
        }

        if (viteBasePath is { } vbp)
        {
            Evidence ev = context.Evidence(vbp.Path, vbp.Line, "base");
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("configuration", "deployment-base-path")), NodeKind.From("Configuration"),
                new[] { ev }, Confidence.From(0.85), Props(("name", "Frontend deployment base path"), ("value", vbp.Value))));
        }

        return Task.CompletedTask;
    }
}
