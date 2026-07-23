using Aip.Abstractions.Analysis;
using Aip.Core.Domain;
using Aip.Infrastructure;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises <see cref="RepositoryScanner"/>'s solution-aware project discovery — when a repo has a
/// .sln/.slnx, only the .csproj files it actually references should become <c>Project</c> artifacts, not
/// every .csproj a naive filesystem glob would find under the repo root (e.g. an unrelated sibling shared
/// library sitting in the same tree).
/// </summary>
public class ScanningTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aip-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        return dir;
    }

    private static void WriteMinimalCsproj(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
    }

    [Fact]
    public async Task Sln_only_includes_referenced_projects_not_every_csproj_in_the_tree()
    {
        string root = NewDir();
        try
        {
            WriteMinimalCsproj(Path.Combine(root, "App.Api", "App.Api.csproj"));
            WriteMinimalCsproj(Path.Combine(root, "App.Core", "App.Core.csproj"));
            // A sibling shared library that exists in the same repo tree but is NOT referenced by the
            // solution — a common real-world shape (an unrelated internal-tools library sitting next to
            // the actual app in the same repo) that a naive filesystem glob would wrongly pick up.
            WriteMinimalCsproj(Path.Combine(root, "Shared.Unrelated", "Shared.Unrelated.csproj"));

            File.WriteAllText(Path.Combine(root, "App.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App.Api", "App.Api\App.Api.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App.Core", "App.Core\App.Core.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{33333333-3333-3333-3333-333333333333}"
                EndProject
                """);

            var scanner = new RepositoryScanner();
            IReadOnlyList<Artifact> artifacts = await scanner.DiscoverAsync(new RepositoryId("app"), root);

            List<string> names = artifacts.Where(a => a.Technology == RepositoryScanner.DotNetProject)
                .Select(a => a.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "App.Api", "App.Core" }, names);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Slnx_only_includes_referenced_projects()
    {
        string root = NewDir();
        try
        {
            WriteMinimalCsproj(Path.Combine(root, "App.Api", "App.Api.csproj"));
            WriteMinimalCsproj(Path.Combine(root, "Shared.Unrelated", "Shared.Unrelated.csproj"));

            File.WriteAllText(Path.Combine(root, "App.slnx"), """
                <Solution>
                  <Project Path="App.Api/App.Api.csproj" />
                </Solution>
                """);

            var scanner = new RepositoryScanner();
            IReadOnlyList<Artifact> artifacts = await scanner.DiscoverAsync(new RepositoryId("app"), root);

            List<string> names = artifacts.Where(a => a.Technology == RepositoryScanner.DotNetProject)
                .Select(a => a.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "App.Api" }, names);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task No_solution_file_falls_back_to_discovering_every_csproj()
    {
        string root = NewDir();
        try
        {
            WriteMinimalCsproj(Path.Combine(root, "App.Api", "App.Api.csproj"));
            WriteMinimalCsproj(Path.Combine(root, "App.Core", "App.Core.csproj"));

            var scanner = new RepositoryScanner();
            IReadOnlyList<Artifact> artifacts = await scanner.DiscoverAsync(new RepositoryId("app"), root);

            List<string> names = artifacts.Where(a => a.Technology == RepositoryScanner.DotNetProject)
                .Select(a => a.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "App.Api", "App.Core" }, names);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
