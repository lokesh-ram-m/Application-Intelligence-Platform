using Aip.Abstractions.Analysis;
using Aip.Core.Domain;

namespace Aip.Infrastructure;

/// <summary>
/// The Repository Scanner: solution, project, and artifact discovery. It walks a materialized
/// repository and classifies its build units into Artifacts a plugin can claim. Unsupported units are
/// left undiscovered here and surfaced as diagnostics by the pipeline.
/// </summary>
public sealed class RepositoryScanner : IArtifactDiscovery
{
    public const string DotNetProject = "dotnet-project";
    public const string AngularWorkspace = "angular-workspace";
    public const string NextWorkspace = "nextjs-workspace";
    public const string ReactWorkspace = "react-workspace";

    public Task<IReadOnlyList<Artifact>> DiscoverAsync(RepositoryId repository, string rootPath, CancellationToken ct = default)
    {
        var artifacts = new List<Artifact>();

        // Project discovery — one artifact per .NET project.
        foreach (string csproj in FindFiles(rootPath, "*.csproj"))
        {
            artifacts.Add(new Artifact(
                repository, csproj, DotNetProject, Path.GetFileNameWithoutExtension(csproj)));
        }

        // Angular workspace discovery — one artifact per angular.json.
        foreach (string ng in FindFiles(rootPath, "angular.json"))
        {
            string name = Path.GetFileName(Path.GetDirectoryName(ng) ?? "angular");
            artifacts.Add(new Artifact(repository, ng, AngularWorkspace, name));
        }

        // Next.js workspace — one artifact per next.config.*; its directory is the workspace root.
        var nextDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cfg in FindFiles(rootPath, "next.config.js").Concat(FindFiles(rootPath, "next.config.mjs")).Concat(FindFiles(rootPath, "next.config.ts")))
        {
            string dir = Path.GetDirectoryName(cfg) ?? rootPath;
            if (nextDirs.Add(dir))
                artifacts.Add(new Artifact(repository, cfg, NextWorkspace, Path.GetFileName(dir)));
        }

        // React workspace — a package.json that depends on react but is NOT a Next app (Next is React too).
        foreach (string pkg in FindFiles(rootPath, "package.json"))
        {
            string dir = Path.GetDirectoryName(pkg) ?? rootPath;
            if (nextDirs.Contains(dir)) continue;                         // already covered by the Next plugin
            string text;
            try { text = File.ReadAllText(pkg); } catch { continue; }
            bool isReact = text.Contains("\"react\"", StringComparison.OrdinalIgnoreCase);
            bool isNext = text.Contains("\"next\"", StringComparison.OrdinalIgnoreCase);
            bool isAngular = text.Contains("@angular/core", StringComparison.OrdinalIgnoreCase);
            if (isReact && !isNext && !isAngular)
                artifacts.Add(new Artifact(repository, pkg, ReactWorkspace, Path.GetFileName(dir)));
        }

        return Task.FromResult<IReadOnlyList<Artifact>>(artifacts);
    }

    private static IEnumerable<string> FindFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                rel.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                continue;
            yield return file;
        }
    }
}
