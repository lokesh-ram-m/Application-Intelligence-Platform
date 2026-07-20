using System.Diagnostics;

using Aip.Abstractions.Analysis;
using Aip.Core.Domain;
using Aip.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises <see cref="GitRepositorySource"/>'s auto-diff logic (the <c>previousCommit</c> parameter of
/// <see cref="IRepositorySource.MaterializeAsync"/>) directly against a real local git repository — the
/// piece <see cref="ExecutionPipeline"/>'s batch-mode auto-diff (see README's "Batch mode" section) relies
/// on to decide which files actually changed since the last analyzed commit.
/// </summary>
public class GitRepositorySourceTests
{
    private static GitRepositorySource Source() => new(GitCredentials.Empty, NullLogger<GitRepositorySource>.Instance);

    private static string NewRepo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aip-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RunGit("init -q -b main", dir);
        RunGit("config user.email test@example.com", dir);
        RunGit("config user.name Test", dir);

        return dir;
    }

    private static string CommitFile(string dir, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(dir, fileName), content);
        RunGit($"add {fileName}", dir);
        RunGit($"commit -q -m commit-{fileName}", dir);

        return RunGit("rev-parse --short HEAD", dir).Trim();
    }

    private static string RunGit(string args, string cwd)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false };
        using Process? p = Process.Start(psi);
        string output = p!.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);

        return output;
    }

    // git marks its own object files read-only on Windows, which Directory.Delete can't remove directly.
    private static void DeleteRepo(string dir)
    {
        foreach (FileInfo file in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Unchanged_commit_does_not_attempt_a_diff()
    {
        string repo = NewRepo();
        string commit = CommitFile(repo, "a.txt", "one");

        RepositoryMaterialization result = await Source().MaterializeAsync(new RepositoryId("r"), repo, previousCommit: commit);

        Assert.Equal(commit, result.Commit.Value);
        Assert.Null(result.ChangedFiles);   // same commit as previousCommit — nothing to diff

        DeleteRepo(repo);
    }

    [Fact]
    public async Task Changed_commit_with_a_reachable_previous_commit_reports_the_changed_files()
    {
        string repo = NewRepo();
        string first = CommitFile(repo, "a.txt", "one");
        CommitFile(repo, "b.txt", "two");   // second commit — only b.txt is new

        RepositoryMaterialization result = await Source().MaterializeAsync(new RepositoryId("r"), repo, previousCommit: first);

        Assert.NotNull(result.ChangedFiles);
        Assert.Contains("b.txt", result.ChangedFiles!);
        Assert.DoesNotContain("a.txt", result.ChangedFiles!);   // untouched since the previous commit

        DeleteRepo(repo);
    }

    [Fact]
    public async Task Changed_commit_with_an_unreachable_previous_commit_reports_unknown_rather_than_throwing()
    {
        string repo = NewRepo();
        CommitFile(repo, "a.txt", "one");
        const string fakeCommit = "0000000000000000000000000000000000dead";   // never existed in this repo

        RepositoryMaterialization result = await Source().MaterializeAsync(new RepositoryId("r"), repo, previousCommit: fakeCommit);

        // Never throws, and never silently reports "nothing changed" — a repository this pipeline can't
        // safely diff must be treated as fully changed by the caller (ExecutionPipeline), not skipped.
        Assert.Null(result.ChangedFiles);

        DeleteRepo(repo);
    }

    [Fact]
    public async Task No_previous_commit_supplied_means_no_diff_is_attempted()
    {
        string repo = NewRepo();
        CommitFile(repo, "a.txt", "one");

        RepositoryMaterialization result = await Source().MaterializeAsync(new RepositoryId("r"), repo, previousCommit: null);

        Assert.Null(result.ChangedFiles);

        DeleteRepo(repo);
    }
}
