using System.Text.RegularExpressions;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;

using static Aip.Core.Domain.IdentitySegment;
using static Aip.Core.Domain.PropertyBag;

namespace Aip.Plugins.Security;

/// <summary>
/// Flags credential-shaped strings sitting in plaintext in pipeline/config files — the same category of
/// finding tools like TruffleHog/GitGuardian/GitHub secret scanning look for, applied generically across
/// whatever pipeline YAML or JSON config files a repo has. Deliberately narrow and text-only: this only
/// ever sees whatever snapshot of files is being analyzed right now, not full git history (a secret
/// committed once and later removed from HEAD won't be caught here — a real limitation, not an oversight).
///
/// Never stores the matched secret value itself anywhere — only which key it was assigned to and where.
/// The whole point of a documentation platform surfacing this is to get someone to rotate the credential;
/// it must not become a second place the credential itself is readable from.
/// </summary>
public sealed class SecretScanAnalyzer : IAnalyzer
{
    public string Name => "secret-scan";

    // A key name that suggests the value assigned to it is a credential, holding a non-trivial literal
    // value — long enough that a real secret (not a single flag or short code) is the plausible read.
    private static readonly Regex CredentialAssignment = new(
        @"[""']?(?<key>\w*(?:token|secret|apikey|api_key|accesskey|access_key|password|pwd)\w*)[""']?\s*[:=]\s*[""']?(?<value>[^\s""'{}$][^\r\n""']{7,})[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A connection string's embedded Password=/Pwd= segment — a different shape from a top-level
    // key/value pair (the credential is one clause inside a larger string), so it needs its own pattern.
    private static readonly Regex ConnectionStringPassword = new(
        @"(?:Password|Pwd)\s*=\s*(?<value>[^;""'\r\n]{4,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Deliberately excludes this project's own (and any analyzed repo's) legitimate placeholder
    // conventions — a token-substitution marker is not a hardcoded secret, it's proof one *isn't* hardcoded.
    // Also excludes short, obviously-fake filler words so "password: changeme" in a sample/test file isn't
    // flagged as a live credential.
    private static readonly Regex PlaceholderValue = new(
        @"^(~~\{.*\}~~|\$\{.*\}|<%.*%>|%\w+%|\*+|changeme|xxx+|todo|your[-_]?\w*|dummy|placeholder|example|test|null|none|redacted)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A connection string's Server/Data Source/Host clause, to check whether it points at the machine
    // running it rather than a real, remotely-reachable server — see LooksLocalOnly below.
    private static readonly Regex ConnectionStringHost = new(
        @"(?:Server|Data Source|Host)\s*=\s*(?<host>[^;,\\""'\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalHostValue = new(
        @"^(localhost|127\.0\.0\.1|::1|0\.0\.0\.0|\(local\)|\.)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (PlainTextModel)context.Model;
        string text = model.Text;
        string fileName = Path.GetFileName(model.Path);

        foreach (Match m in CredentialAssignment.Matches(text))
        {
            string value = m.Groups["value"].Value.Trim();
            if (PlaceholderValue.IsMatch(value)) continue;
            // A password/secret-shaped key=value pair sitting on the same line as a connection string's
            // own Server=/Host=/Data Source= clause is the same false-positive category as the dedicated
            // ConnectionStringPassword check below (both regexes can independently match the same
            // substring — CredentialAssignment's `[:=]` separator matches a connection string's `=` too).
            if (LooksLocalOnly(LineContaining(text, m.Index))) continue;
            Emit(context, sink, fileName, text, m.Index, m.Groups["key"].Value);
        }

        foreach (Match m in ConnectionStringPassword.Matches(text))
        {
            string value = m.Groups["value"].Value.Trim();
            if (PlaceholderValue.IsMatch(value)) continue;
            if (LooksLocalOnly(LineContaining(text, m.Index))) continue;
            Emit(context, sink, fileName, text, m.Index, "connection string password");
        }

        return Task.CompletedTask;
    }

    // A connection string that can only ever reach the machine it's running on isn't a real exposure,
    // regardless of what password it uses — this is standard practice for shipping default local-dev
    // config (Docker Compose-style setups, template repos), not a leaked production credential. Checking
    // the actual host beats denylisting dummy words one at a time ("password", "postgres", "changeme", ...)
    // since it catches any placeholder value as long as the connection genuinely can't leave the box.
    private static bool LooksLocalOnly(string line)
    {
        Match host = ConnectionStringHost.Match(line);
        if (!host.Success) return false;
        string value = host.Groups["host"].Value.Trim();
        int portSplit = value.IndexOfAny(new[] { ':', ',' });
        if (portSplit >= 0) value = value[..portSplit];

        return LocalHostValue.IsMatch(value.Trim());
    }

    private static string LineContaining(string text, int index)
    {
        int start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        int end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;

        return text[start..end];
    }

    private static void Emit(IAnalysisContext context, IDiscoverySink sink, string fileName, string text, int matchIndex, string key)
    {
        int line = LineAt(text, matchIndex);
        Evidence ev = context.Evidence(fileName, line, key);
        sink.Add(NodeDiscovery.Create(
            context.NodeId(Seg("vulnerability", $"{fileName}:{line}:{key}")), NodeKind.From("Vulnerability"),
            new[] { ev }, Confidence.From(0.7),
            Props(("type", "hardcoded-credential"), ("file", fileName), ("key", key), ("severity", "high"))));
    }

    private static int LineAt(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;

        return line;
    }
}
