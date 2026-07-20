using Aip.Abstractions.Engines;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Engines.TypeScript;

/// <summary>A single TypeScript source file (path + text) in the semantic model.</summary>
public sealed record TsFile(string Path, string Text)
{
    // Line 1 for index 0 — shared across every analyzer that reports a source location, so a match found
    // via any regex/scan counts lines the same way regardless of which analyzer or plugin found it.
    public static int LineAt(string text, int index) =>
        text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;
}

/// <summary>
/// A TypeScript semantic model for one workspace. <see cref="Parser"/> reports whether ts-morph or the
/// heuristic fallback produced it, so downstream facts carry honest provenance.
/// </summary>
public sealed class TypeScriptSemanticModel : ISemanticModel
{
    internal TypeScriptSemanticModel(string parser, IReadOnlyList<TsFile> files)
    {
        Parser = parser;
        Files = files;
    }

    public string Parser { get; }
    public IReadOnlyList<TsFile> Files { get; }
}

/// <summary>
/// The TypeScript / JavaScript Language Engine. One engine serves every TS framework (Angular, React,
/// Next.js, NestJS). It reads source with a heuristic (regex) reader — there is no Roslyn-equivalent AST
/// parser for TypeScript on .NET. A future ts-morph sidecar (Node) would upgrade this to full AST parsing;
/// the <see cref="TypeScriptSemanticModel.Parser"/> provenance is set accordingly. It knows the language,
/// never a framework.
/// </summary>
internal sealed class TypeScriptLanguageEngine : ILanguageEngine
{
    public string Language => "typescript";

    // No caching here at all — every call re-reads files fresh from disk, so this engine has no stale-cache
    // risk in a long-lived process and doesn't need the commit for anything (see ILanguageEngine's doc
    // comment for why Roslyn's engine, which does cache, needs it).
    public Task<ISemanticModel> BuildModelAsync(string artifactPath, string? commit = null, CancellationToken ct = default)
    {
        string root = Path.GetDirectoryName(artifactPath) ?? artifactPath;
        var files = new List<TsFile>();

        foreach (string file in EnumerateSources(root))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(new TsFile(file, File.ReadAllText(file)));
        }

        return Task.FromResult<ISemanticModel>(new TypeScriptSemanticModel("heuristic", files));
    }

    // *.component.html (Angular's own template-file naming convention), not a bare "*.html" — that would
    // also sweep in every framework's static shell files (CRA's public/index.html, Next's public/*.html),
    // which carry no component-composition signal and would just be noise for every other frontend plugin
    // sharing this engine. Angular's real templates read the exact same way regardless of the workspace's
    // component vs. project layout convention, since the match is purely on the filename suffix.
    private static readonly string[] Extensions = { "*.ts", "*.tsx", "*.js", "*.jsx", "*.component.html" };

    private static IEnumerable<string> EnumerateSources(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (string pattern in Extensions)
            foreach (string file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}.next{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}")) continue;
                if (file.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".config.js", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".config.mjs", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
    }
}

public static class TypeScriptEngineModule
{
    public static IServiceCollection AddAipTypeScriptEngine(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageEngine, TypeScriptLanguageEngine>();

        return services;
    }
}
