using Aip.Abstractions.Engines;

namespace Aip.Plugins.Security;

/// <summary>
/// The whole raw text of one file — nothing parsed, nothing structured. The secret-scanner only needs to
/// pattern-match text; there's no meaningful "language" underneath a pipeline YAML or a JSON config file
/// the way there is for C# or TypeScript, so this deliberately isn't a real semantic model.
/// </summary>
public sealed class PlainTextModel : ISemanticModel
{
    public PlainTextModel(string path, string text)
    {
        Path = path;
        Text = text;
    }

    public string Parser => "plaintext";
    public string Path { get; }
    public string Text { get; }
}

/// <summary>
/// The trivial "language engine" for plaintext config/pipeline files — reads one file, verbatim, no
/// parsing. Exists only so <c>SecretScanAnalyzer</c> can go through the same plugin/engine dispatch every
/// other analyzer does (see <c>ExecutionPipeline.AnalyzeArtifactAsync</c>), rather than needing a special
/// case in the pipeline for "this one plugin doesn't need a real engine."
/// </summary>
public sealed class PlainTextLanguageEngine : ILanguageEngine
{
    public string Language => "plaintext";

    public Task<ISemanticModel> BuildModelAsync(string artifactPath, string? commit = null, CancellationToken ct = default)
    {
        string text;
        try { text = File.ReadAllText(artifactPath); }
        catch (IOException) { text = string.Empty; }

        return Task.FromResult<ISemanticModel>(new PlainTextModel(artifactPath, text));
    }
}
