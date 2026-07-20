namespace Aip.Core.Domain;

/// <summary>How a fact was established. Determinism is the default; probabilistic is the exception.</summary>
public enum ExtractionMethod
{
    Deterministic,
    Probabilistic
}

/// <summary>A version-control commit identifier — part of every piece of evidence.</summary>
public readonly record struct Commit
{
    public string Value { get; }

    public Commit(string value) => Value = Guard.NotNullOrWhiteSpace(value, nameof(Commit));

    public override string ToString() => Value;
}

/// <summary>
/// Where in the source a fact was observed. Path/line/symbol are provenance, never identity —
/// the same fact keeps its identity when the file moves (Session 2 §3).
/// </summary>
public sealed record SourceLocation
{
    public string File { get; }
    public int? Line { get; }
    public string? Symbol { get; }

    private SourceLocation(string file, int? line, string? symbol)
    {
        File = file;
        Line = line;
        Symbol = symbol;
    }

    public static SourceLocation Create(string file, int? line = null, string? symbol = null)
    {
        Guard.NotNullOrWhiteSpace(file, nameof(file));
        Guard.Requires(line is null or >= 0, "Line must be non-negative.");

        return new SourceLocation(file, line, symbol);
    }

    public override string ToString() =>
        Line is null ? File : $"{File}:{Line}";
}

/// <summary>
/// The grounding for a single fact (Session 2 §4). Evidence is what separates knowledge from rumor
/// and is the unit of incremental invalidation: a changed file names exactly which facts to re-derive.
/// </summary>
public sealed record Evidence
{
    public RepositoryId Repository { get; }
    public Commit Commit { get; }
    public string Engine { get; }
    public ExtractionMethod Method { get; }
    public Confidence Confidence { get; }
    public SourceLocation? Location { get; }

    private Evidence(RepositoryId repository, Commit commit, string engine, ExtractionMethod method, Confidence confidence, SourceLocation? location)
    {
        Repository = repository;
        Commit = commit;
        Engine = engine;
        Method = method;
        Confidence = confidence;
        Location = location;
    }

    public static Evidence Create(
        RepositoryId repository,
        Commit commit,
        string engine,
        ExtractionMethod method,
        Confidence confidence,
        SourceLocation? location = null)
    {
        Guard.NotNullOrWhiteSpace(engine, nameof(engine));

        return new Evidence(repository, commit, engine, method, confidence, location);
    }
}
