using System.Collections.Concurrent;

using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Abstractions.Plugins;

namespace Aip.Analysis;

/// <summary>
/// Hosts the language engines and caches one semantic model per (language, artifact) so multiple
/// plugins reuse the same parse — a first line of duplicate-work avoidance.
/// </summary>
internal sealed class LanguageEngineHost : ILanguageEngineHost
{
    private readonly Dictionary<string, ILanguageEngine> _byLanguage;
    private readonly ConcurrentDictionary<string, Task<ISemanticModel>> _cache = new();

    public LanguageEngineHost(IEnumerable<ILanguageEngine> engines)
    {
        _byLanguage = engines.ToDictionary(e => e.Language, StringComparer.OrdinalIgnoreCase);
    }

    public bool Supports(string language) => _byLanguage.ContainsKey(language);

    public Task<ISemanticModel> GetModelAsync(string language, string artifactPath, string? commit = null, CancellationToken ct = default)
    {
        if (!_byLanguage.TryGetValue(language, out ILanguageEngine? engine))
            throw new InvalidOperationException($"No language engine registered for '{language}'.");

        // The commit is part of the cache key, not just the path — this host (and the process it lives
        // in, via serve mode) can outlive a single execution, so a stale entry from an earlier commit of
        // the same local-path repository must never be handed back for a later one.
        return _cache.GetOrAdd($"{language}|{artifactPath}|{commit}", _ => engine.BuildModelAsync(artifactPath, commit, ct));
    }
}

/// <summary>
/// The Plugin Host / loader. Orders plugins dependency-first (topological over manifest Dependencies,
/// with Priority as the tie-break) and routes each artifact to the plugins that claim it.
/// </summary>
internal sealed class PluginHost : IPluginHost
{
    private readonly IReadOnlyList<IPlugin> _ordered;

    public PluginHost(IEnumerable<IPlugin> plugins)
    {
        _ordered = Order(plugins.ToList());
        Manifests = _ordered.Select(p => p.Manifest).ToList();
    }

    // Computed once at construction, not per access — plugins never change after startup, and the
    // Schema Registry now reads this on every single Validation pass (see InMemorySchemaRegistry).
    public IReadOnlyList<PluginManifest> Manifests { get; }

    public IReadOnlyList<IPlugin> SelectFor(Artifact artifact) =>
        _ordered.Where(p => p.Manifest.Claims(artifact.Technology)).ToList();

    private static IReadOnlyList<IPlugin> Order(List<IPlugin> plugins)
    {
        var byId = plugins.ToDictionary(p => p.Manifest.Id);
        var visited = new HashSet<string>();
        var result = new List<IPlugin>();

        void Visit(IPlugin plugin)
        {
            if (!visited.Add(plugin.Manifest.Id)) return;
            foreach (string dependency in plugin.Manifest.Dependencies)
                if (byId.TryGetValue(dependency, out IPlugin? dep))
                    Visit(dep);
            result.Add(plugin);
        }

        foreach (IPlugin plugin in plugins.OrderByDescending(p => p.Manifest.Priority))
            Visit(plugin);

        return result;
    }
}
