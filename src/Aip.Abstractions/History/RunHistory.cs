namespace Aip.Abstractions.History;

/// <summary>One repository materialized during a run — the exact commit analyzed, for incremental reuse.
/// <paramref name="SourceKind"/> is a string ("Local" | "PublicGit" | "PrivateGit") rather than the
/// Analysis-layer enum, so this store stays free of a dependency on Aip.Abstractions.Analysis.</summary>
public sealed record RepositoryRunInfo(
    string Application,
    string RepositoryName,
    string Location,
    string? Branch,
    string SourceKind,
    string CommitSha);

/// <summary>A durable record of one pipeline execution: what ran, against which commits, at what AI cost.</summary>
public sealed record RunHistoryRecord(
    Guid RunId,
    string Application,
    string TriggerType,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? AiProvider,
    string? AiModel,
    int PromptTokens,
    int CompletionTokens,
    int PagesGenerated,
    int KnowledgeNodeCount,
    int RelationshipCount,
    IReadOnlyList<RepositoryRunInfo> Repositories);

/// <summary>
/// Durable history of pipeline runs — separate from the Knowledge Store (which holds the graph itself).
/// Answers "what ran, when, against which commit, and at what token cost" without needing the full
/// knowledge graph loaded. <see cref="GetLastCommitAsync"/> is what <c>skipIfUnchanged</c> reads to decide
/// whether a repository has moved since its last analyzed commit, letting a batch run skip whole
/// applications with nothing to re-analyze.
/// </summary>
public interface IRunHistoryStore
{
    Task<Guid> BeginRunAsync(string application, string triggerType, DateTimeOffset startedAt, CancellationToken ct = default);

    Task RecordRepositoryAsync(
        Guid runId, string application, string repositoryName, string location, string? branch, string sourceKind,
        string commitSha, CancellationToken ct = default);

    Task CompleteRunAsync(
        Guid runId, string status, DateTimeOffset completedAt, string? aiProvider, string? aiModel,
        int promptTokens, int completionTokens, int pagesGenerated, int knowledgeNodeCount, int relationshipCount,
        CancellationToken ct = default);

    Task<string?> GetLastCommitAsync(string application, string repositoryLocation, CancellationToken ct = default);

    Task<IReadOnlyList<RunHistoryRecord>> GetRecentRunsAsync(string? application, int limit, CancellationToken ct = default);
}
