using Aip.Abstractions.Registries;
using Aip.Infrastructure;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aip.Host;

/// <summary>
/// Loads the estate declaration (<c>apps.yml</c>) into <see cref="ApplicationDescriptor"/>s — the single
/// source of truth for which applications and repositories exist, read fresh on every batch run (or every
/// <c>/run</c> call in <c>serve</c> mode) so the file can be edited without a redeploy. Repo entries are
/// git URLs (shallow-cloned by the repository source) or local paths; relative local paths are resolved
/// against the config file's directory so <c>apps.yml</c> is portable.
/// </summary>
internal static class AppsFile
{
    public static IReadOnlyList<ApplicationDescriptor> Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config not found: {configPath}. Pass --config <file> or add apps.yml at the repo root.");

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        AppsConfig cfg = deserializer.Deserialize<AppsConfig>(File.ReadAllText(configPath)) ?? new AppsConfig();
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Directory.GetCurrentDirectory();

        List<AppEntry> entries = cfg.Applications.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
        ValidateChildren(entries);

        return entries
            .Select(a => new ApplicationDescriptor(
                a.Name,
                a.Repos.Select(r => ResolveRepo(r, baseDir)).ToList(),
                a.SkipIfUnchanged,
                a.Children,
                a.ForceReanalysis))
            .ToList();
    }

    // The single validation gate for composition: every referenced child must exist, and the
    // Name -> Children graph must be acyclic (a composite can never, even transitively, contain itself).
    // Nothing downstream (PlatformRunner's topological sort, ExecutionPipeline's child-snapshot pull-in)
    // re-checks either invariant — they can assume a valid DAG.
    private static void ValidateChildren(List<AppEntry> entries)
    {
        var byName = entries.ToDictionary(a => a.Name, a => a);
        foreach (AppEntry entry in entries)
            foreach (string child in entry.Children)
                if (!byName.ContainsKey(child))
                    throw new InvalidOperationException($"apps.yml: '{entry.Name}' declares child '{child}', which is not a declared application.");

        var state = new Dictionary<string, int>(); // 0 = unvisited, 1 = in-progress (gray), 2 = done (black)
        var path = new List<string>();
        foreach (AppEntry entry in entries)
            Visit(entry.Name, byName, state, path);
    }

    private static void Visit(string name, Dictionary<string, AppEntry> byName, Dictionary<string, int> state, List<string> path)
    {
        if (state.GetValueOrDefault(name) == 2) return;
        if (state.GetValueOrDefault(name) == 1)
            throw new InvalidOperationException($"apps.yml: cycle in application composition: {string.Join(" -> ", path.SkipWhile(n => n != name))} -> {name}");

        state[name] = 1;
        path.Add(name);
        foreach (string child in byName[name].Children)
            Visit(child, byName, state, path);
        path.RemoveAt(path.Count - 1);
        state[name] = 2;
    }

    // Git URLs pass through untouched; local paths are made absolute relative to the config file.
    private static string ResolveRepo(string repo, string baseDir)
    {
        if (GitRepositorySource.LooksLikeGitUrl(repo) || Path.IsPathRooted(repo)) return repo;
        string candidate = Path.GetFullPath(Path.Combine(baseDir, repo));

        return Directory.Exists(candidate) ? candidate : repo;
    }

    // ---- apps.yml shape (mirrors DocPlatform) ----
    private sealed class AppsConfig
    {
        public List<AppEntry> Applications { get; set; } = new();
    }

    private sealed class AppEntry
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Repos { get; set; } = new();
        // When true, a batch run skips this application entirely (no clone-and-analyze, no AI cost) if
        // every repository's current commit matches the last one recorded in Run History.
        public bool SkipIfUnchanged { get; set; } = false;
        // Names of other applications declared in this same file whose Knowledge Models are merged into
        // this one — see ApplicationDescriptor.Children.
        public List<string> Children { get; set; } = new();
        // When true, every repository is treated as fully changed regardless of its actual commit, so
        // incremental pruning never skips an artifact — see ApplicationDescriptor.ForceReanalysis.
        public bool ForceReanalysis { get; set; } = false;
    }
}
