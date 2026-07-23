using System.Text.RegularExpressions;
using System.Xml.Linq;

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
    public const string SecurityScanTarget = "security-scan-target";

    public Task<IReadOnlyList<Artifact>> DiscoverAsync(RepositoryId repository, string rootPath, CancellationToken ct = default)
    {
        var artifacts = new List<Artifact>();

        // Project discovery — one artifact per .NET project. When the repo has a .sln/.slnx, only projects
        // it actually references count as part of the solution — a sibling .csproj sitting in the same
        // repo tree but not referenced by it (a shared library used by some other app, old scaffolding, a
        // sample project, ...) is not this app's architecture, even though a naive filesystem glob would
        // find it too. DiscoverSolutionProjects returns null (not an empty set) when no solution file
        // exists anywhere in the tree, so repos without one keep today's "every .csproj found" behavior.
        HashSet<string>? solutionProjects = DiscoverSolutionProjects(rootPath);
        foreach (string csproj in FindFiles(rootPath, "*.csproj"))
        {
            if (solutionProjects is not null && !solutionProjects.Contains(NormalizePath(csproj))) continue;
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

        // Security scan targets — pipeline definitions and app config files, one artifact per file. Not
        // discovered for any other purpose today (no other plugin claims .yml/.yaml, and appsettings*.json
        // is read directly by config binding at runtime, never scanned as a knowledge-model artifact), so
        // this is net-new surface, not a broadening of an existing glob.
        foreach (string file in FindFiles(rootPath, "*.yml").Concat(FindFiles(rootPath, "*.yaml")).Concat(FindFiles(rootPath, "appsettings*.json")))
            artifacts.Add(new Artifact(repository, file, SecurityScanTarget, Path.GetFileName(file)));

        return Task.FromResult<IReadOnlyList<Artifact>>(artifacts);
    }

    // Classic .sln project lines look like:
    //   Project("{9A19103F-...}") = "MyApp.API", "MyApp.API\MyApp.API.csproj", "{8B494DE8-...}"
    // Solution-folder entries use the same shape but their "path" field is just a folder name, never
    // ending in .csproj, so filtering on that suffix is enough to skip them without special-casing the
    // folder GUID. A full MSBuild solution parser is unwarranted for extracting one field like this.
    private static readonly Regex SlnProjectLine =
        new(@"Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""[^""]*"",\s*""([^""]+\.csproj)""", RegexOptions.Compiled);

    // Returns the set of .csproj absolute paths referenced by any .sln/.slnx found under rootPath, or null
    // if none exists anywhere in the tree — null is the "no solution file, don't filter" signal, distinct
    // from an empty set (a real solution that references zero .csproj, which correctly excludes everything).
    private static HashSet<string>? DiscoverSolutionProjects(string rootPath)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool foundAny = false;

        foreach (string sln in FindFiles(rootPath, "*.sln"))
        {
            foundAny = true;
            string dir = Path.GetDirectoryName(sln) ?? rootPath;
            string text;
            try { text = File.ReadAllText(sln); } catch (IOException) { continue; }
            foreach (Match m in SlnProjectLine.Matches(text))
                AddResolved(referenced, dir, m.Groups[1].Value);
        }

        foreach (string slnx in FindFiles(rootPath, "*.slnx"))
        {
            foundAny = true;
            string dir = Path.GetDirectoryName(slnx) ?? rootPath;
            try
            {
                XDocument doc = XDocument.Load(slnx);
                foreach (XElement el in doc.Descendants("Project"))
                {
                    string? path = el.Attribute("Path")?.Value;
                    if (path is { Length: > 0 } && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        AddResolved(referenced, dir, path);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { /* malformed .slnx — skip it, other solution files (if any) still apply */ }
        }

        return foundAny ? referenced : null;
    }

    private static void AddResolved(HashSet<string> set, string baseDir, string relativePath) =>
        set.Add(NormalizePath(Path.Combine(baseDir, relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))));

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

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
