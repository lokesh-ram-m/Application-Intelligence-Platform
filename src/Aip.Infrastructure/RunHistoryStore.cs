using System.Text.Json;

using Aip.Abstractions.History;
using Aip.Abstractions.Knowledge;
using Aip.Core.Abstractions;
using Aip.Core.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aip.Infrastructure;

internal sealed class RunEntity
{
    public Guid Id { get; set; }
    public string Application { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "Running";
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int PagesGenerated { get; set; }
    public int KnowledgeNodeCount { get; set; }
    public int RelationshipCount { get; set; }
    public List<RepositoryRunEntity> Repositories { get; set; } = new();
}

internal sealed class RepositoryRunEntity
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    // Denormalized (also reachable via Run.Application) so this table is self-sufficient when browsed
    // directly — no join needed to see which app a repository row belongs to.
    public string Application { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string Location { get; set; } = "";
    public string? Branch { get; set; }
    // "Local" | "PublicGit" | "PrivateGit" — PrivateGit means a configured Git:Credentials PAT was used
    // to authenticate the clone; see RepositorySourceKind in Aip.Abstractions.Analysis.
    public string SourceKind { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public DateTimeOffset MaterializedAt { get; set; }
    public RunEntity Run { get; set; } = null!;
}

internal sealed class AiFallbackEventEntity
{
    public long Id { get; set; }
    public string Application { get; set; } = "";
    // Comma-joined repository names — an app can span more than one repo, and no single AI call site
    // today has one specific repo in scope (see AiFallbackEvent). Denormalized text rather than a child
    // table since this is written far more often than queried, and queries group by Application anyway.
    public string Repositories { get; set; } = "";
    public string Section { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class DocumentVersionChangeEntity
{
    public long Id { get; set; }
    public string Application { get; set; } = "";
    public int VersionNumber { get; set; }
    public int PreviousVersionNumber { get; set; }
    public int NodesAdded { get; set; }
    public int NodesRemoved { get; set; }
    public int RelationshipsAdded { get; set; }
    public int RelationshipsRemoved { get; set; }
    // Capped name lists + repository commit deltas — read as a whole per version, never filtered at the
    // list-item level, so JSON text columns (matching SnapshotEntity's own approach) fit better than a
    // child table here.
    public string AddedNodeNamesJson { get; set; } = "[]";
    public string RemovedNodeNamesJson { get; set; } = "[]";
    public string AddedRelationshipNamesJson { get; set; } = "[]";
    public string RemovedRelationshipNamesJson { get; set; } = "[]";
    public string RepositoryCommitsJson { get; set; } = "[]";
    public string Summary { get; set; } = "";
    public bool AiWritten { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class SnapshotEntity
{
    public Guid Id { get; set; }
    public string Application { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string NodesJson { get; set; } = "[]";
    public string RelationshipsJson { get; set; } = "[]";
}

/// <summary>
/// EF Core model for the Run History store. SQL Server / Azure SQL only — a local Docker SQL Server
/// container in dev, Azure SQL in production, both via the same <c>UseSqlServer</c> connection string
/// configured in <see cref="InfrastructureModule"/>. The schema and every query here are identical
/// either way; only the connection string changes.
/// </summary>
internal sealed class RunHistoryDbContext : DbContext
{
    public RunHistoryDbContext(DbContextOptions<RunHistoryDbContext> options) : base(options) { }

    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<RepositoryRunEntity> RepositoryRuns => Set<RepositoryRunEntity>();
    public DbSet<AiFallbackEventEntity> AiFallbackEvents => Set<AiFallbackEventEntity>();
    public DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();
    public DbSet<DocumentVersionChangeEntity> DocumentVersionChanges => Set<DocumentVersionChangeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Application);
            e.HasMany(r => r.Repositories).WithOne(r => r.Run).HasForeignKey(r => r.RunId);
        });

        modelBuilder.Entity<RepositoryRunEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Location);
            e.HasIndex(r => r.Application);
        });

        modelBuilder.Entity<AiFallbackEventEntity>(e =>
        {
            e.HasKey(r => r.Id);
            // "How many times has this happened for a particular repo/app" is the query this index serves.
            e.HasIndex(r => r.Application);
        });

        modelBuilder.Entity<SnapshotEntity>(e =>
        {
            e.HasKey(s => s.Id);
            // Serves "the latest snapshot for this application" (GetSnapshotAsync) — every read except a
            // direct by-id lookup goes through this shape.
            e.HasIndex(s => new { s.Application, s.CreatedAt });
            e.Property(s => s.NodesJson).HasColumnType("nvarchar(max)");
            e.Property(s => s.RelationshipsJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<DocumentVersionChangeEntity>(e =>
        {
            e.HasKey(c => c.Id);
            // "The change record for this app's version N" — the read this table exists to serve.
            e.HasIndex(c => new { c.Application, c.VersionNumber });
            e.Property(c => c.Summary).HasColumnType("nvarchar(max)");
        });
    }
}

/// <summary>
/// <see cref="IRunHistoryStore"/> backed by <see cref="RunHistoryDbContext"/>. Uses a context factory
/// rather than an injected context because this store is registered as a singleton (matching every other
/// infrastructure adapter) while a <see cref="DbContext"/> itself is never safe to share across concurrent
/// calls — each method opens a short-lived context of its own.
/// </summary>
internal sealed class EfRunHistoryStore : IRunHistoryStore
{
    private readonly IDbContextFactory<RunHistoryDbContext> _factory;

    public EfRunHistoryStore(IDbContextFactory<RunHistoryDbContext> factory) => _factory = factory;

    public async Task<Guid> BeginRunAsync(string application, string triggerType, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        var entity = new RunEntity
        {
            Id = Guid.NewGuid(),
            Application = application,
            TriggerType = triggerType,
            StartedAt = startedAt,
            Status = "Running"
        };
        db.Runs.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity.Id;
    }

    public async Task RecordRepositoryAsync(
        Guid runId, string application, string repositoryName, string location, string? branch, string sourceKind,
        string commitSha, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        db.RepositoryRuns.Add(new RepositoryRunEntity
        {
            RunId = runId,
            Application = application,
            RepositoryName = repositoryName,
            Location = location,
            Branch = branch,
            SourceKind = sourceKind,
            CommitSha = commitSha,
            MaterializedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteRunAsync(
        Guid runId, string status, DateTimeOffset completedAt, string? aiProvider, string? aiModel,
        int promptTokens, int completionTokens, int pagesGenerated, int knowledgeNodeCount, int relationshipCount,
        CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        RunEntity? run = await db.Runs.FindAsync(new object[] { runId }, ct);
        if (run is null) return;

        run.Status = status;
        run.CompletedAt = completedAt;
        run.AiProvider = aiProvider;
        run.AiModel = aiModel;
        run.PromptTokens = promptTokens;
        run.CompletionTokens = completionTokens;
        run.PagesGenerated = pagesGenerated;
        run.KnowledgeNodeCount = knowledgeNodeCount;
        run.RelationshipCount = relationshipCount;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetLastCommitAsync(string application, string repositoryLocation, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        RepositoryRunEntity? match = await db.RepositoryRuns
            .Where(r => r.Location == repositoryLocation && r.Run.Application == application)
            .OrderByDescending(r => r.MaterializedAt)
            .FirstOrDefaultAsync(ct);

        return match?.CommitSha;
    }

    public async Task<IReadOnlyList<RunHistoryRecord>> GetRecentRunsAsync(string? application, int limit, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        IQueryable<RunEntity> query = db.Runs.Include(r => r.Repositories);
        if (!string.IsNullOrWhiteSpace(application))
            query = query.Where(r => r.Application == application);

        List<RunEntity> rows = await query.OrderByDescending(r => r.StartedAt).Take(limit).ToListAsync(ct);

        return rows.Select(r => new RunHistoryRecord(
            r.Id, r.Application, r.TriggerType, r.StartedAt, r.CompletedAt, r.Status, r.AiProvider, r.AiModel,
            r.PromptTokens, r.CompletionTokens, r.PagesGenerated, r.KnowledgeNodeCount, r.RelationshipCount,
            r.Repositories.Select(x => new RepositoryRunInfo(x.Application, x.RepositoryName, x.Location, x.Branch, x.SourceKind, x.CommitSha)).ToList())).ToList();
    }
}

/// <summary><see cref="IAiFallbackStore"/> backed by the same <see cref="RunHistoryDbContext"/>/database
/// as Run History — see <see cref="AiFallbackEventEntity"/> for why it's one denormalized table rather
/// than a child of Runs. Also mirrors every event into the structured log (Warning — the page still
/// rendered, just deterministically, so this is a degraded-but-handled condition, not a failure) at this
/// single choke point, the same pattern ExecutionPipeline.FinishAsync uses for Diagnostics — so a fallback
/// shows up in the ordinary log stream without every caller needing to log it separately.</summary>
internal sealed class EfAiFallbackStore : IAiFallbackStore
{
    private readonly IDbContextFactory<RunHistoryDbContext> _factory;
    private readonly ILogger<EfAiFallbackStore> _log;

    public EfAiFallbackStore(IDbContextFactory<RunHistoryDbContext> factory, ILogger<EfAiFallbackStore> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task RecordAsync(AiFallbackEvent evt, CancellationToken ct = default)
    {
        _log.LogWarning("AI fallback: {Application} [{Section}] repos={Repositories} reason={Reason} — {Detail}",
            evt.Application, evt.Section, string.Join(", ", evt.Repositories), evt.Reason, evt.Detail);

        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        db.AiFallbackEvents.Add(new AiFallbackEventEntity
        {
            Application = evt.Application,
            Repositories = string.Join(", ", evt.Repositories),
            Section = evt.Section,
            Reason = evt.Reason,
            Detail = evt.Detail,
            OccurredAt = evt.OccurredAt
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// <see cref="IKnowledgeStore"/> backed by the same <see cref="RunHistoryDbContext"/>/database as Run
/// History — one row per committed Snapshot (append-only; a row is never updated or deleted), with
/// nodes/relationships serialized via <see cref="SnapshotSerialization"/>. Replaces the earlier local-disk
/// JSON store now that the platform runs unattended/standalone: a snapshot committed by one run must be
/// readable by the next run regardless of which machine or container instance executes it.
/// </summary>
internal sealed class EfKnowledgeRepository : IKnowledgeStore
{
    private readonly IDbContextFactory<RunHistoryDbContext> _factory;

    public EfKnowledgeRepository(IDbContextFactory<RunHistoryDbContext> factory) => _factory = factory;

    public async Task<Snapshot?> GetSnapshotAsync(ApplicationId application, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        SnapshotEntity? entity = await db.Snapshots
            .Where(s => s.Application == application.Value)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<Snapshot> CommitAsync(
        ApplicationId application, IReadOnlyList<KnowledgeNode> nodes, IReadOnlyList<Relationship> relationships, CancellationToken ct = default)
    {
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), application, DateTimeOffset.UtcNow, nodes, relationships);
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        db.Snapshots.Add(new SnapshotEntity
        {
            Id = snapshot.Id.Value,
            Application = application.Value,
            CreatedAt = snapshot.CreatedAt,
            NodesJson = SnapshotSerialization.SerializeNodes(nodes),
            RelationshipsJson = SnapshotSerialization.SerializeRelationships(relationships)
        });
        await db.SaveChangesAsync(ct);

        return snapshot;
    }

    public async Task<SnapshotDiff> DiffAsync(SnapshotId from, SnapshotId to, CancellationToken ct = default)
    {
        Snapshot a = await GetByIdAsync(from, ct) ?? throw new InvalidOperationException($"Unknown snapshot {from}.");
        Snapshot b = await GetByIdAsync(to, ct) ?? throw new InvalidOperationException($"Unknown snapshot {to}.");

        return SnapshotDiffing.Diff(a, b);
    }

    public async Task<Snapshot?> GetByIdAsync(SnapshotId id, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        SnapshotEntity? entity = await db.Snapshots.FindAsync(new object[] { id.Value }, ct);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<Snapshot>> GetHistoryAsync(ApplicationId application, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        List<SnapshotEntity> rows = await db.Snapshots
            .Where(s => s.Application == application.Value)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        return rows.Select(ToDomain).ToList();
    }

    private static Snapshot ToDomain(SnapshotEntity e) => Snapshot.Create(
        new SnapshotId(e.Id), new ApplicationId(e.Application), e.CreatedAt,
        SnapshotSerialization.DeserializeNodes(e.NodesJson), SnapshotSerialization.DeserializeRelationships(e.RelationshipsJson));
}

/// <summary><see cref="IVersionChangeStore"/> backed by the same <see cref="RunHistoryDbContext"/>/database
/// as Run History and the Knowledge Store — one row per published version that had a predecessor to diff
/// against (never for v1). Backs the Viewer's "What Changed" page.</summary>
internal sealed class EfVersionChangeStore : IVersionChangeStore
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly IDbContextFactory<RunHistoryDbContext> _factory;

    public EfVersionChangeStore(IDbContextFactory<RunHistoryDbContext> factory) => _factory = factory;

    public async Task RecordAsync(DocumentVersionChange change, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        db.DocumentVersionChanges.Add(new DocumentVersionChangeEntity
        {
            Application = change.Application,
            VersionNumber = change.VersionNumber,
            PreviousVersionNumber = change.PreviousVersionNumber,
            NodesAdded = change.NodesAdded,
            NodesRemoved = change.NodesRemoved,
            RelationshipsAdded = change.RelationshipsAdded,
            RelationshipsRemoved = change.RelationshipsRemoved,
            AddedNodeNamesJson = JsonSerializer.Serialize(change.AddedNodeNames, Json),
            RemovedNodeNamesJson = JsonSerializer.Serialize(change.RemovedNodeNames, Json),
            AddedRelationshipNamesJson = JsonSerializer.Serialize(change.AddedRelationshipNames, Json),
            RemovedRelationshipNamesJson = JsonSerializer.Serialize(change.RemovedRelationshipNames, Json),
            RepositoryCommitsJson = JsonSerializer.Serialize(change.RepositoryCommits, Json),
            Summary = change.Summary,
            AiWritten = change.AiWritten,
            OccurredAt = change.OccurredAt
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<DocumentVersionChange?> GetAsync(string application, int versionNumber, CancellationToken ct = default)
    {
        await using RunHistoryDbContext db = await _factory.CreateDbContextAsync(ct);
        DocumentVersionChangeEntity? e = await db.DocumentVersionChanges
            .Where(x => x.Application == application && x.VersionNumber == versionNumber)
            .FirstOrDefaultAsync(ct);

        return e is null ? null : new DocumentVersionChange(
            e.Application, e.VersionNumber, e.PreviousVersionNumber,
            e.NodesAdded, e.NodesRemoved, e.RelationshipsAdded, e.RelationshipsRemoved,
            JsonSerializer.Deserialize<List<string>>(e.AddedNodeNamesJson, Json) ?? new(),
            JsonSerializer.Deserialize<List<string>>(e.RemovedNodeNamesJson, Json) ?? new(),
            JsonSerializer.Deserialize<List<string>>(e.AddedRelationshipNamesJson, Json) ?? new(),
            JsonSerializer.Deserialize<List<string>>(e.RemovedRelationshipNamesJson, Json) ?? new(),
            JsonSerializer.Deserialize<List<RepositoryCommitChange>>(e.RepositoryCommitsJson, Json) ?? new(),
            e.Summary, e.AiWritten, e.OccurredAt);
    }
}
