namespace Aip.Core.Domain;

public enum DiagnosticSeverity
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// A record of a problem or limitation encountered during analysis. Honesty is a domain value: the
/// platform surfaces what it could not understand rather than silently omitting it.
/// </summary>
public sealed record Diagnostic
{
    public DiagnosticSeverity Severity { get; }
    public string Message { get; }
    public string Source { get; }

    private Diagnostic(DiagnosticSeverity severity, string message, string source)
    {
        Severity = severity;
        Message = message;
        Source = source;
    }

    public static Diagnostic Create(DiagnosticSeverity severity, string message, string source)
    {
        Guard.NotNullOrWhiteSpace(message, nameof(message));
        Guard.NotNullOrWhiteSpace(source, nameof(source));

        return new Diagnostic(severity, message, source);
    }

    public static Diagnostic Debug(string message, string source) => Create(DiagnosticSeverity.Debug, message, source);
    public static Diagnostic Info(string message, string source) => Create(DiagnosticSeverity.Info, message, source);
    public static Diagnostic Warning(string message, string source) => Create(DiagnosticSeverity.Warning, message, source);
    public static Diagnostic Error(string message, string source) => Create(DiagnosticSeverity.Error, message, source);
}

/// <summary>
/// Well-known <see cref="Diagnostic.Source"/> values that more than one project needs to agree on — e.g.
/// <c>Aip.Analysis</c>'s <c>ExecutionPipeline</c> emits pipeline-level diagnostics under this tag, and
/// <c>Aip.Host</c>'s <c>PlatformRunner</c> filters for exactly that tag when deciding what to echo to the
/// console. A shared const here (rather than the same string literal typed twice across a project
/// boundary) keeps the two from silently drifting apart.
/// </summary>
public static class DiagnosticSources
{
    public const string Pipeline = "pipeline";
}
