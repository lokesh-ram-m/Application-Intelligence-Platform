using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Core.Domain;

namespace Aip.Analysis;

/// <summary>Thread-safe collector of the immutable Discoveries and Diagnostics emitted during a run.</summary>
internal sealed class DiscoverySink : IDiscoverySink
{
    private readonly List<Discovery> _discoveries = new();
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly object _gate = new();

    public void Add(Discovery discovery)
    {
        lock (_gate) _discoveries.Add(discovery);
    }

    public void Report(Diagnostic diagnostic)
    {
        lock (_gate) _diagnostics.Add(diagnostic);
    }

    public IReadOnlyList<Discovery> Discoveries { get { lock (_gate) return _discoveries.ToList(); } }
    public IReadOnlyList<Diagnostic> Diagnostics { get { lock (_gate) return _diagnostics.ToList(); } }
}

/// <summary>
/// The concrete analysis context. Centralizes identity minting (app/repo hierarchy) and evidence
/// creation so every analyzer is consistent and deterministic.
/// </summary>
internal sealed class AnalysisContext : IAnalysisContext
{
    public AnalysisContext(
        ExecutionId executionId,
        ExecutionScope scope,
        Artifact artifact,
        RepositoryId repository,
        Commit commit,
        string engine,
        ISemanticModel model)
    {
        ExecutionId = executionId;
        Scope = scope;
        Artifact = artifact;
        Repository = repository;
        Commit = commit;
        Engine = engine;
        Model = model;
    }

    public ExecutionId ExecutionId { get; }
    public ExecutionScope Scope { get; }
    public Artifact Artifact { get; }
    public RepositoryId Repository { get; }
    public Commit Commit { get; }
    public string Engine { get; }
    public ISemanticModel Model { get; }

    public KnowledgeIdentity NodeId(params IdentitySegment[] tail)
    {
        KnowledgeIdentity id = KnowledgeIdentity.ForApplication(Scope.Application)
            .Append(new IdentitySegment("repo", Repository.Value));
        foreach (IdentitySegment segment in tail)
            id = id.Append(segment);

        return id;
    }

    public KnowledgeIdentity AppNodeId(params IdentitySegment[] tail)
    {
        KnowledgeIdentity id = KnowledgeIdentity.ForApplication(Scope.Application);
        foreach (IdentitySegment segment in tail)
            id = id.Append(segment);

        return id;
    }

    public Evidence Evidence(string? file = null, int? line = null, string? symbol = null)
    {
        SourceLocation? location = file is null ? null : SourceLocation.Create(file, line, symbol);

        return Core.Domain.Evidence.Create(
            Repository, Commit, Engine, ExtractionMethod.Deterministic, Confidence.Full, location);
    }
}
