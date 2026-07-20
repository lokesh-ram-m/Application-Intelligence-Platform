using System.Diagnostics;
using System.Text;

using Aip.Abstractions.Analysis;
using Aip.Core.Domain;

using Microsoft.Extensions.Logging;

namespace Aip.Infrastructure;

/// <summary>
/// Git host (e.g. <c>dev.azure.com</c>) → personal access token, for cloning private repositories. Keyed
/// by host, not by repo — one Azure DevOps or GitHub PAT covers every repo on that host, so tokens never
/// need repeating per <c>apps.yml</c> entry. The token authenticates via an HTTP header at clone time and
/// is never embedded in a URL, so it can't leak through a printed repo location, a git remote config, or
/// a diagnostic message that happens to echo the location back.
/// </summary>
public sealed record GitCredentials(IReadOnlyDictionary<string, string> TokensByHost)
{
    public static readonly GitCredentials Empty = new(new Dictionary<string, string>());

    public string? TokenFor(string url)
    {
        try { return TokensByHost.TryGetValue(new Uri(url).Host, out string? t) ? t : null; }
        catch (UriFormatException) { return null; }
    }
}

/// <summary>
/// Git-backed repository discovery. A local path is used in place; a git URL is shallow-cloned. A branch
/// may be requested with a <c>url#branch</c> suffix (e.g. <c>…/repo.git#master</c>); otherwise the
/// repository's default branch is used. Private repos authenticate via <see cref="GitCredentials"/>,
/// matched by host — no per-repo configuration needed. The resolved commit is recorded so every
/// downstream fact is anchored to a known version.
/// </summary>
public sealed class GitRepositorySource : IRepositorySource
{
    private readonly GitCredentials _credentials;
    private readonly ILogger<GitRepositorySource> _log;

    public GitRepositorySource(GitCredentials credentials, ILogger<GitRepositorySource> log)
    {
        _credentials = credentials;
        _log = log;
    }

    public async Task<RepositoryMaterialization> MaterializeAsync(RepositoryId repository, string location, string? previousCommit = null, CancellationToken ct = default)
    {
        (string source, string? branch) = SplitBranch(location);
        string root;
        RepositorySourceKind sourceKind;
        bool isLocal;
        if (Directory.Exists(source))
        {
            root = Path.GetFullPath(source);
            sourceKind = RepositorySourceKind.Local;
            isLocal = true;
        }
        else if (LooksLikeGitUrl(source))
        {
            root = await CloneAsync(source, branch, ct);
            sourceKind = _credentials.TokenFor(source) is null ? RepositorySourceKind.PublicGit : RepositorySourceKind.PrivateGit;
            isLocal = false;
        }
        else
        {
            throw new DirectoryNotFoundException($"Repository location not found: {source}");
        }

        Commit commit = ReadCommit(root);
        IReadOnlyList<string>? changedFiles = previousCommit is null || previousCommit == commit.Value
            ? null
            : await TryComputeChangedFilesAsync(root, source, previousCommit, needsFetch: !isLocal, ct);

        return new RepositoryMaterialization(repository, root, commit, sourceKind, changedFiles);
    }

    // Computes the file names changed between previousCommit and the just-materialized HEAD. Never throws —
    // any failure (previous commit unreachable after a force-push/rebase/GC, network issue on the extra
    // fetch, …) is caught and reported as "unknown" (null), which callers must treat as "assume everything
    // under this repository changed" rather than silently under-reporting a real change.
    private async Task<IReadOnlyList<string>?> TryComputeChangedFilesAsync(
        string root, string sourceUrl, string previousCommit, bool needsFetch, CancellationToken ct)
    {
        try
        {
            if (needsFetch)
            {
                // The standard materialization is a --depth 1 shallow clone into a fresh directory every
                // run, so previousCommit isn't present in the local object database yet. Fetching just that
                // one commit object (not full history) is enough to diff against it locally afterward.
                await RunGitCheckedAsync($"{AuthArg(sourceUrl)}fetch --depth 1 origin {previousCommit}", root, ct);
            }

            string output = await RunGitCheckedAsync($"diff {previousCommit} HEAD --name-only", root, ct);

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Splits a "url#branch" location into its URL and optional branch. Local paths pass through.</summary>
    internal static (string Source, string? Branch) SplitBranch(string location)
    {
        int hash = location.LastIndexOf('#');

        return hash < 0 ? (location, null) : (location[..hash], location[(hash + 1)..]);
    }

    /// <summary>Whether a repo location looks like a git URL rather than a local path — shared with
    /// <c>AppsFile</c>, which needs the same test to decide whether a relative <c>apps.yml</c> entry is
    /// a path to resolve or a URL to pass through untouched.</summary>
    public static bool LooksLikeGitUrl(string location) =>
        location.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        location.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

    private async Task<string> CloneAsync(string url, string? branch, CancellationToken ct)
    {
        string target = Path.Combine(Path.GetTempPath(), "aip-sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        string branchArg = string.IsNullOrWhiteSpace(branch) ? "" : $"--branch {branch} ";

        await RunGitAsync($"{AuthArg(url)}clone --depth 1 {branchArg}{url} \"{target}\"", Path.GetTempPath(), ct);

        // A shallow clone leaves any git submodule as an empty placeholder gitlink, not real content —
        // that needs an explicit init/update. Best-effort: a repo with no .gitmodules is a no-op; if a
        // submodule needs different credentials than its parent (submodules are usually on the same host,
        // so the same token/header applies) this may still fail, in which case analysis proceeds without
        // that submodule's content rather than failing the whole repo outright.
        if (File.Exists(Path.Combine(target, ".gitmodules")))
        {
            try { await RunGitAsync($"{AuthArg(url)}submodule update --init --recursive", target, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Git submodule init failed for {Url} — continuing without it", url); }
        }

        return target;
    }

    private static Commit ReadCommit(string root)
    {
        try
        {
            string sha = RunGit("rev-parse --short HEAD", root);

            return new Commit(string.IsNullOrWhiteSpace(sha) ? "workingcopy" : sha.Trim());
        }
        catch
        {
            return new Commit("workingcopy");
        }
    }

    private static string RunGit(string args, string workingDir)
    {
        // Only stdout is redirected — stderr is not read here, so redirecting it too would risk the
        // same unread-pipe-buffer deadlock the async overload below avoids deliberately.
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using Process? p = Process.Start(psi);
        if (p is null) return string.Empty;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);

        return output;
    }

    // Surfaces a failed clone as an exception (e.g. a bad/missing PAT) instead of silently leaving an
    // empty/partial target directory — never includes `args` in the message, since it may carry the
    // auth header; only git's own stderr (which does not echo credentials back) is included.
    private static async Task RunGitAsync(string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process? p = Process.Start(psi);
        if (p is null) return;
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git clone failed (exit {p.ExitCode}): {stderr.Trim()}");
    }

    // Like RunGitAsync, but returns stdout and treats a non-zero exit as failure — needed for diff/fetch,
    // where an empty stdout on success (nothing changed) must be distinguishable from an empty stdout on
    // failure (e.g. diffing against a commit git doesn't have). Both streams are read concurrently rather
    // than one after the other, to avoid the same unread-pipe-buffer deadlock RunGit's own comment warns
    // about — reading stdout to completion before starting to drain stderr risks the process blocking on a
    // full stderr buffer while this method is still waiting on stdout that will never arrive.
    private static async Task<string> RunGitCheckedAsync(string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process? p = Process.Start(psi);
        if (p is null) throw new InvalidOperationException("Failed to start git.");
        Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = p.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git command failed (exit {p.ExitCode}): {(await stderrTask).Trim()}");

        return await stdoutTask;
    }

    // Authenticate via an HTTP header (empty username, PAT as password — the standard Basic-auth pattern
    // for both GitHub and Azure DevOps PATs) rather than embedding the token in the URL.
    private string AuthArg(string url)
    {
        string? token = _credentials.TokenFor(url);

        return token is null ? "" : $"-c http.extraheader=\"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token}"))}\" ";
    }
}
