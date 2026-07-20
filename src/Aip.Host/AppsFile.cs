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

        return cfg.Applications
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new ApplicationDescriptor(
                a.Name,
                a.Repos.Select(r => ResolveRepo(r, baseDir)).ToList(),
                a.SkipIfUnchanged))
            .ToList();
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
    }
}
