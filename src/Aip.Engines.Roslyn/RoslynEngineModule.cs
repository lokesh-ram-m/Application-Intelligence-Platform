using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Aip.Abstractions.Engines;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aip.Engines.Roslyn;

/// <summary>
/// Registers the MSBuild toolset (via Microsoft.Build.Locator) exactly once, before any
/// Microsoft.Build.*/Microsoft.CodeAnalysis.MSBuild type is touched anywhere in the process — a hard
/// ordering requirement of MSBuildLocator. A module initializer is the one mechanism the CLR guarantees
/// runs before the first use of anything else in this assembly, so this can't be raced by DI registration
/// order or which analyzer happens to run first.
/// </summary>
internal static class MsBuildBootstrap
{
    // CA2255 flags ModuleInitializer as unusual in library code — correct in general, but this is exactly
    // the documented, Microsoft-recommended pattern for MSBuildLocator: it must run before any consumer of
    // this assembly touches an MSBuild/CodeAnalysis.MSBuild type, and a module initializer is the only
    // mechanism that's guaranteed regardless of which analyzer or host happens to run first.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Init()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }
#pragma warning restore CA2255
}

/// <summary>
/// A Roslyn semantic model for one .NET project. The <see cref="Compilation"/> is <b>solution-wide</b> (it
/// includes every project under the same .sln) so analyzers can resolve symbols across project boundaries;
/// <see cref="Trees"/> exposes only the current project's files, so nodes are still emitted per project.
/// Plugins downcast <see cref="ISemanticModel"/> to this.
/// </summary>
public sealed class RoslynSemanticModel : ISemanticModel
{
    private readonly IReadOnlyList<SyntaxTree> _ownTrees;

    internal RoslynSemanticModel(CSharpCompilation compilation, IReadOnlyList<SyntaxTree> ownTrees)
    {
        Compilation = compilation;
        _ownTrees = ownTrees;
    }

    public string Parser => "roslyn";
    public CSharpCompilation Compilation { get; }

    /// <summary>The current project's syntax trees (for node emission); resolution still spans the whole solution.</summary>
    public IReadOnlyList<SyntaxTree> Trees => _ownTrees;

    public SemanticModel GetSemanticModel(SyntaxTree tree) => Compilation.GetSemanticModel(tree);

    public string PathOf(SyntaxTree tree) => tree.FilePath;
}

/// <summary>
/// The C# / .NET Language Engine. Loads the real project via MSBuildWorkspace (backed by an actual
/// <c>dotnet restore</c>) so the compilation includes the analyzed repo's own NuGet package assemblies —
/// not just the .NET BCL. This is what lets analyzers resolve real types/methods from third-party SDKs
/// (e.g. which class actually calls <c>BlobServiceClient.UploadAsync</c>) via genuine semantic symbols,
/// instead of guessing from `using` directive text and a hand-maintained method-name whitelist. If restore
/// or workspace loading fails for any reason (no network, a private feed needing auth, an unsupported
/// solution format), this degrades gracefully to a BCL-only ad-hoc compilation — syntax-level analysis
/// still works, only symbol resolution of external types is lost for that run.
/// </summary>
internal sealed class RoslynLanguageEngine : ILanguageEngine
{
    private static readonly IReadOnlyList<MetadataReference> ReferenceAssemblies = LoadReferenceAssemblies();
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(5);

    // One loaded scope (solution or standalone project) per solution/project path, reused across every
    // artifact in that scope. Lazy<T> inside the dictionary guarantees the expensive restore+load work
    // (which can take real wall-clock time on a cold NuGet cache) runs exactly once even under concurrent
    // callers, rather than racing multiple redundant restores.
    private static readonly ConcurrentDictionary<string, Lazy<LoadedScope>> ScopeCache = new(StringComparer.OrdinalIgnoreCase);

    public string Language => "csharp";

    // Guard rails so a solution-wide load can never blow up on a huge or oddly-laid-out tree.
    private const int MaxSolutionFiles = 1200;   // above this, fall back to a per-project scope
    private const int MaxSlnWalkLevels = 5;      // how far up to look for a .sln

    // Solution-wide cross-project resolution is ON by default (validated fast on 8- and 24-project
    // solutions). Disable with AIP_CROSS_PROJECT=0 to force fast per-project analysis.
    private static readonly bool CrossProjectEnabled =
        (Environment.GetEnvironmentVariable("AIP_CROSS_PROJECT") ?? "").Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    private readonly ILogger<RoslynLanguageEngine> _log;

    public RoslynLanguageEngine(ILogger<RoslynLanguageEngine> log) => _log = log;

    public Task<ISemanticModel> BuildModelAsync(string artifactPath, string? commit = null, CancellationToken ct = default)
    {
        string projectDir = Path.GetDirectoryName(artifactPath) ?? artifactPath;

        string? slnPath = CrossProjectEnabled ? FindSolutionFile(projectDir) : null;
        if (slnPath is not null && EnumerateSources(Path.GetDirectoryName(slnPath)!).Take(MaxSolutionFiles + 1).Count() > MaxSolutionFiles)
            slnPath = null;   // too large for a solution-wide load — fall back to this project alone

        // The commit is part of the key, not just the path — ScopeCache is process-static and this engine
        // is reused across every artifact of every run for the lifetime of the process (including a
        // long-lived `serve` process handling many /run calls), so a stale entry from a local-path repo's
        // earlier commit must never be handed back once that repo has genuinely moved on.
        string scopeKey = $"{slnPath ?? artifactPath}|{commit}";
        LoadedScope scope = ScopeCache.GetOrAdd(scopeKey,
            _ => new Lazy<LoadedScope>(() => LoadScope(slnPath, artifactPath, _log, ct), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        CSharpCompilation compilation = ResolveCompilation(scope, artifactPath, projectDir, _log, ct);

        // The fallback compilation covers the whole solution scope (every project's files in one bag),
        // unlike a real per-project Compilation from MSBuildWorkspace which already only contains its own
        // files. Without this prefix filter, every project in a fallback scope would see every OTHER
        // project's files too — this was a real bug: it's what produced identical "N of N" file counts
        // across every project in a failed-restore solution.
        string projectPrefix = projectDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var ownTrees = compilation.SyntaxTrees
            .Where(t => IsRelevantSource(t.FilePath))
            .Where(t => t.FilePath.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _log.LogDebug("Roslyn model built for {Artifact}: {OwnFiles} own file(s) of {TotalFiles} in its compilation",
            Path.GetFileName(artifactPath), ownTrees.Count, compilation.SyntaxTrees.Count());

        return Task.FromResult<ISemanticModel>(new RoslynSemanticModel(compilation, ownTrees));
    }

    private static CSharpCompilation ResolveCompilation(LoadedScope scope, string artifactPath, string projectDir, ILogger log, CancellationToken ct)
    {
        if (scope.Fallback is not null) return scope.Fallback;

        Project? project = scope.Solution is not null
            ? scope.Solution.Projects.FirstOrDefault(p => string.Equals(p.FilePath, artifactPath, StringComparison.OrdinalIgnoreCase))
            : scope.StandaloneProject;

        Compilation? compilation = project?.GetCompilationAsync(ct).GetAwaiter().GetResult();
        if (compilation is CSharpCompilation csc) return csc;

        // The specific project wasn't found in an opened solution, or MSBuildWorkspace produced a
        // non-C# compilation somehow — fall back rather than lose this artifact's analysis entirely.
        log.LogWarning("Could not resolve a real compilation for {ArtifactPath} — using BCL-only fallback", artifactPath);

        return BuildFallbackCompilation(projectDir, ct);
    }

    private sealed class LoadedScope
    {
        public Solution? Solution;                 // set when the scope is a .sln (cross-project)
        public Project? StandaloneProject;          // set when the scope is a single .csproj
        public CSharpCompilation? Fallback;          // set when real MSBuild loading failed entirely
    }

    private static LoadedScope LoadScope(string? slnPath, string artifactPath, ILogger log, CancellationToken ct)
    {
        string restoreTarget = slnPath ?? artifactPath;
        string projectDir = Path.GetDirectoryName(artifactPath) ?? artifactPath;

        (bool restored, string restoreLog) = TryRestore(restoreTarget);
        if (!restored)
        {
            log.LogWarning("'dotnet restore {RestoreTarget}' failed — falling back to BCL-only analysis " +
                "(external package types won't resolve for this scope). {RestoreLog}", restoreTarget, restoreLog);

            return new LoadedScope { Fallback = BuildFallbackCompilation(slnPath is not null ? Path.GetDirectoryName(slnPath)! : projectDir, ct) };
        }

        try
        {
            MSBuildWorkspace workspace = MSBuildWorkspace.Create();
            // MSBuildWorkspace reports partial-load problems (e.g. one bad project in a big solution) here
            // without throwing — the workspace and any successfully-loaded projects remain usable, so this
            // is surfaced for visibility only, never treated as a hard failure.
            workspace.WorkspaceFailed += (_, e) => log.LogWarning("{DiagnosticKind}: {DiagnosticMessage}", e.Diagnostic.Kind, e.Diagnostic.Message);

            if (slnPath is not null)
            {
                // .slnx (the newer XML solution format) isn't recognized by this Roslyn Workspaces
                // version's solution-file parser at all — "No file format header found" — even though
                // `dotnet restore` itself handles .slnx fine. Rather than making (and logging) a doomed
                // attempt every single time, skip straight to opening its projects individually: they
                // still resolve against each other via ProjectReferences, since MSBuildWorkspace
                // accumulates every opened project into one shared CurrentSolution regardless of how each
                // one was added. Classic .sln still goes through OpenSolutionAsync normally — that format
                // is supported — with the same individual-project path as a resilience fallback if it
                // fails for some other reason (e.g. a genuinely malformed solution file).
                if (slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                    return new LoadedScope { Solution = OpenProjectsIndividually(workspace, Path.GetDirectoryName(slnPath)!, log, ct) };

                try
                {
                    Solution solution = workspace.OpenSolutionAsync(slnPath, cancellationToken: ct).GetAwaiter().GetResult();

                    return new LoadedScope { Solution = solution };
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not parse solution file {SlnPath} — opening its projects " +
                        "individually instead (cross-project resolution is preserved)", slnPath);

                    return new LoadedScope { Solution = OpenProjectsIndividually(workspace, Path.GetDirectoryName(slnPath)!, log, ct) };
                }
            }

            Project loadedProject = workspace.OpenProjectAsync(artifactPath, cancellationToken: ct).GetAwaiter().GetResult();

            return new LoadedScope { StandaloneProject = loadedProject };
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "MSBuildWorkspace failed to load {RestoreTarget} — falling back to BCL-only analysis", restoreTarget);

            return new LoadedScope { Fallback = BuildFallbackCompilation(slnPath is not null ? Path.GetDirectoryName(slnPath)! : projectDir, ct) };
        }
    }

    // Opens every .csproj under a solution directory into the same workspace instance instead of asking
    // MSBuildWorkspace to parse the solution file at all. Cross-project resolution still works: opening
    // one project auto-follows its own ProjectReferences and loads those too, and MSBuildWorkspace
    // accumulates every opened project into one shared CurrentSolution regardless of how each got there.
    private static Solution OpenProjectsIndividually(MSBuildWorkspace workspace, string solutionDir, ILogger log, CancellationToken ct)
    {
        foreach (string csproj in Directory.EnumerateFiles(solutionDir, "*.csproj", SearchOption.AllDirectories))
        {
            // By the time this loop reaches a project some earlier one already referenced, it's already
            // in workspace.CurrentSolution — that's success, not a failure to report.
            if (workspace.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, csproj, StringComparison.OrdinalIgnoreCase)))
                continue;
            try { workspace.OpenProjectAsync(csproj, cancellationToken: ct).GetAwaiter().GetResult(); }
            catch (Exception pex) { log.LogWarning(pex, "Could not open project {Csproj}", csproj); }
        }

        return workspace.CurrentSolution;
    }

    // Runs the analyzed repo's own restore so its NuGet package DLLs land in the local cache / obj folder
    // where MSBuildWorkspace can find them — a fresh shallow clone has neither, since bin/obj are always
    // gitignored. Requires network access to NuGet the first time a given repo/package set is seen;
    // subsequent runs reuse the shared NuGet cache and are fast. Returns the process's own output on
    // failure — a silent bool here would leave every restore failure undebuggable.
    private static (bool Success, string Log) TryRestore(string target)
    {
        try
        {
            // NuGetAudit=false / TreatWarningsAsErrors=false: this restore is read-only fact-gathering on
            // someone else's repository, never a build we intend to ship — their own CI-hardening choice
            // to escalate NuGet security-audit warnings (NU1902/NU1903) to hard errors must not block us
            // from simply resolving package metadata. Overridden via MSBuild properties, not by touching
            // any file in the analyzed repo.
            var psi = new ProcessStartInfo("dotnet",
                $"restore \"{target}\" -p:NuGetAudit=false -p:TreatWarningsAsErrors=false -p:WarningsAsErrors=")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p is null) return (false, "'dotnet' failed to start.");

            // Drain both streams concurrently with waiting — reading one to completion before the process
            // exits (as a sequential ReadToEnd() would) can deadlock if the *other* stream's OS pipe buffer
            // fills up meanwhile, since the child then blocks writing to it forever.
            Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)RestoreTimeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }

                return (false, $"Timed out after {RestoreTimeout.TotalMinutes:0}m.");
            }
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            return (p.ExitCode == 0, p.ExitCode == 0 ? "" : $"Exit code {p.ExitCode}.\n{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);   // e.g. "dotnet" not on PATH — vanishingly unlikely for a tool that itself runs via dotnet
        }
    }

    // The old hand-rolled compilation — BCL references only, no NuGet package resolution. Kept as the
    // safety net when restore/workspace loading fails, so a network hiccup degrades analysis quality
    // rather than crashing the run.
    private static CSharpCompilation BuildFallbackCompilation(string scope, CancellationToken ct)
    {
        var trees = new List<SyntaxTree>();
        foreach (string file in EnumerateSources(scope))
        {
            ct.ThrowIfCancellationRequested();
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file));
        }

        return CSharpCompilation.Create(
            assemblyName: "adhoc-" + Path.GetFileName(scope.TrimEnd(Path.DirectorySeparatorChar)),
            syntaxTrees: trees,
            references: ReferenceAssemblies,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // Walk up (bounded) from a project directory to the nearest .sln/.slnx file (the solution scope).
    private static string? FindSolutionFile(string projectDir)
    {
        var dir = new DirectoryInfo(projectDir);
        for (int levels = 0; dir is not null && levels < MaxSlnWalkLevels; levels++, dir = dir.Parent)
        {
            FileInfo? sln = dir.GetFiles("*.sln").FirstOrDefault() ?? dir.GetFiles("*.slnx").FirstOrDefault();
            if (sln is not null) return sln.FullName;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSources(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            if (IsRelevantSource(file)) yield return file;
    }

    // Excludes build output and generated code (huge EF migration snapshots and *.g.cs files carry no
    // hand-written facts and would bloat both the fallback compilation and node emission either way).
    private static bool IsRelevantSource(string filePath)
    {
        char sep = Path.DirectorySeparatorChar;
        if (filePath.Contains($"{sep}bin{sep}") || filePath.Contains($"{sep}obj{sep}")) return false;
        if (filePath.Contains($"{sep}Migrations{sep}", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static IReadOnlyList<MetadataReference> LoadReferenceAssemblies()
    {
        var refs = new List<MetadataReference>();
        string? tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (tpa is not null)
        {
            foreach (string path in tpa.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    try { refs.Add(MetadataReference.CreateFromFile(path)); }
                    catch { /* skip unreadable references — syntax analysis still works */ }
                }
            }
        }

        return refs;
    }
}

public static class RoslynEngineModule
{
    public static IServiceCollection AddAipRoslynEngine(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageEngine, RoslynLanguageEngine>();

        return services;
    }
}
