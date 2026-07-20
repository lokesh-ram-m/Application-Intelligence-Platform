namespace Aip.Abstractions.Engines;

/// <summary>
/// A language-neutral, queryable semantic model produced by a Language Engine. Concrete engines
/// expose a richer, engine-specific type (RoslynSemanticModel, TypeScriptSemanticModel) that plugins
/// of that language downcast to.
/// </summary>
public interface ISemanticModel
{
    /// <summary>Name of the parser that produced this model (e.g. "roslyn", "ts-morph", "heuristic").</summary>
    string Parser { get; }
}

/// <summary>
/// A Language Engine turns source into a semantic model for ONE language, shared by all plugins of
/// that language (Roslyn for C#, ts-morph for TypeScript). It knows the language, never a framework.
/// </summary>
public interface ILanguageEngine
{
    string Language { get; }

    /// <summary>
    /// Builds a semantic model for the artifact at <paramref name="artifactPath"/>. <paramref name="commit"/>
    /// identifies which version of that path this call is for — engines that cache a loaded model per path
    /// (e.g. Roslyn's MSBuildWorkspace scope, expensive to rebuild) must fold it into their cache key,
    /// otherwise a long-lived host (see <c>Aip.Host</c>'s <c>serve</c> mode) would keep returning a stale
    /// model for a local-path repository forever after its first analysis, never seeing later commits.
    /// </summary>
    Task<ISemanticModel> BuildModelAsync(string artifactPath, string? commit = null, CancellationToken ct = default);
}

/// <summary>
/// Hosts the language engines and builds/caches a semantic model per (language, artifact, commit).
/// Plugins never touch engines directly — the host resolves the engine a plugin's manifest declares.
/// </summary>
public interface ILanguageEngineHost
{
    bool Supports(string language);
    Task<ISemanticModel> GetModelAsync(string language, string artifactPath, string? commit = null, CancellationToken ct = default);
}
