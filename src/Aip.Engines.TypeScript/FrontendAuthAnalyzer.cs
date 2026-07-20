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

    private static readonly Regex LocalStorageToken =
        new(@"localStorage\s*\.\s*(?:setItem|getItem)\s*\(\s*[`'""]([^`'""]*(?:token|jwt|auth)[^`'""]*)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SessionStorageToken =
        new(@"sessionStorage\s*\.\s*(?:setItem|getItem)\s*\(\s*[`'""]([^`'""]*(?:token|jwt|auth)[^`'""]*)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CookieToken =
        new(@"(?:document\.cookie\s*=|Cookies\s*\.\s*set|CookieService\b(?:[^;]*)\.\s*set)[^;\n]{0,80}(?:token|jwt|auth)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var storageLocations = new Dictionary<string, (string File, int Line)>();
        var attachPatterns = new Dictionary<string, (string File, int Line)>();

        foreach (TsFile file in model.Files)
        {
            foreach (Match m in ImportFrom.Matches(file.Text))
            {
                string src = m.Groups[1].Value;
                foreach ((string pattern, string name) in IdentityProviders)
                    if (!providers.ContainsKey(name) && Regex.IsMatch(src, pattern, RegexOptions.IgnoreCase))
                        providers[name] = (file.Path, LineAt(file.Text, m.Index));
            }

            if (!storageLocations.ContainsKey("localStorage") && LocalStorageToken.Match(file.Text) is { Success: true } ls)
                storageLocations["localStorage"] = (file.Path, LineAt(file.Text, ls.Index));
            if (!storageLocations.ContainsKey("sessionStorage") && SessionStorageToken.Match(file.Text) is { Success: true } ss)
                storageLocations["sessionStorage"] = (file.Path, LineAt(file.Text, ss.Index));
            if (!storageLocations.ContainsKey("cookie") && CookieToken.Match(file.Text) is { Success: true } ck)
                storageLocations["cookie"] = (file.Path, LineAt(file.Text, ck.Index));

            if (!attachPatterns.ContainsKey("Authorization: Bearer header") && AuthHeaderAttach.Match(file.Text) is { Success: true } ah)
                attachPatterns["Authorization: Bearer header"] = (file.Path, LineAt(file.Text, ah.Index));
            if (!attachPatterns.ContainsKey("Axios request interceptor") && AxiosInterceptor.Match(file.Text) is { Success: true } ax)
                attachPatterns["Axios request interceptor"] = (file.Path, LineAt(file.Text, ax.Index));
            if (!attachPatterns.ContainsKey("Angular HttpInterceptor") && AngularInterceptorClass.Match(file.Text) is { Success: true } ng)
                attachPatterns["Angular HttpInterceptor"] = (file.Path, LineAt(file.Text, ng.Index));
        }

        foreach ((string name, (string path, int line)) in providers)
        {
            Evidence ev = context.Evidence(path, line, name);
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("authprovider", name)), NodeKind.From("AuthProvider"),
                new[] { ev }, Confidence.From(0.85), Props(("name", name))));
        }

        foreach ((string location, (string path, int line)) in storageLocations)
        {
            Evidence ev = context.Evidence(path, line, location);
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("tokenstorage", location)), NodeKind.From("TokenStorage"),
                new[] { ev }, Confidence.From(0.75), Props(("location", location))));
        }

        foreach ((string pattern, (string path, int line)) in attachPatterns)
        {
            Evidence ev = context.Evidence(path, line, pattern);
            sink.Add(NodeDiscovery.Create(context.NodeId(Seg("tokenattachment", pattern)), NodeKind.From("TokenAttachment"),
                new[] { ev }, Confidence.From(0.75), Props(("pattern", pattern))));
        }

        return Task.CompletedTask;
    }
}
