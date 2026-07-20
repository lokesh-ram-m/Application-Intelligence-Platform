using Aip.Core.Abstractions;
using Aip.Core.Domain;

namespace Aip.Abstractions.Knowledge;

/// <summary>
/// Extended read surface over the versioned Knowledge Store (history, by-id retrieval). Extends the
/// Core repository port without changing it — the write contract remains <see cref="IKnowledgeRepository"/>.
/// </summary>
public interface IKnowledgeStore : IKnowledgeRepository
{
    Task<Snapshot?> GetByIdAsync(SnapshotId id, CancellationToken ct = default);
    Task<IReadOnlyList<Snapshot>> GetHistoryAsync(ApplicationId application, CancellationToken ct = default);
}
