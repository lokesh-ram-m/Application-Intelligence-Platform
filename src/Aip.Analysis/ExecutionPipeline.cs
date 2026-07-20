using System.Diagnostics;
using System.Text.Json;

using Aip.Abstractions.Ai;
using Aip.Abstractions.Analysis;
using Aip.Abstractions.Documents;
using Aip.Abstractions.Engines;
using Aip.Abstractions.History;
using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Observability;
using Aip.Abstractions.Plugins;
using Aip.Abstractions.Projections;
using Aip.Abstractions.Registries;
using Aip.Abstractions.Validation;
using Aip.Core.Abstractions;
using Aip.Core.Domain;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aip.Analysis;

/// <summary>
/// The one execution pipeline shared by every mode. Full flow:
/// resolve scope → materialize → discover artifacts (pruned when incremental) → analyze → Discoveries
/// → Validation → commit Snapshot → Relationship Resolution → Validation → final Snapshot → Projection
/// → Publish → Execution Result. Only the Validation Pipeline creates Knowledge.
/// </summary>
internal sealed class ExecutionPipeline : IAnalysisPipeline
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Diagnostic source tags used more than once — a single spelling to typo-proof, not a full taxonomy.
    // PipelineSource specifically must match Aip.Core.Domain.DiagnosticSources.Pipeline, since
    // PlatformRunner (Aip.Host) filters on that exact tag.
    private const string PipelineSource = DiagnosticSources.Pipeline;
    private const string EngineHostSource = "engine-host";

    private readonly IApplicationRegistry _registry;
    private readonly IRepositorySource _source;
    private readonly IArtifactDiscovery _scanner;
    private readonly ILanguageEngineHost _engines;
    private readonly IPluginHost _plugins;
    private readonly IExecutionStore _executionStore;
    private readonly IValidationPipeline _validation;
    private readonly IRelationshipResolutionEngine _resolution;
    private readonly IKnowledgeRepository _knowledge;
    private readonly IProjectionEngine _projections;
    private readonly IDocumentStore _documentStore;
    private readonly IExecutionReporter _reporter;
    private readonly IRunHistoryStore _runHistory;
    private readonly IVersionChangeStore _versionChanges;
    private readonly IVersionChangelogGenerator _changelog;
    private readonly ITokenAccountant _tokens;
    private readonly IAiProvider _aiProvider;
    private readonly ILogger<ExecutionPipeline> _log;

    public ExecutionPipeline(
        IApplicationRegistry registry, IRepositorySource source, IArtifactDiscovery scanner,
        ILanguageEngineHost engines, IPluginHost plugins, IExecutionStore executionStore,
        IValidationPipeline validation, IRelationshipResolutionEngine resolution, IKnowledgeRepository knowledge,
        IProjectionEngine projections, IDocumentStore documentStore, IExecutionReporter reporter,
        IRunHistoryStore runHistory, IVersionChangeStore versionChanges, IVersionChangelogGenerator changelog,
        ITokenAccountant tokens, IAiProvider aiProvider, ILogger<ExecutionPipeline> log)
    {
        _registry = registry; _source = source; _scanner = scanner;
        _engines = engines; _plugins = plugins; _executionStore = executionStore;
        _validation = validation; _resolution = resolution; _knowledge = knowledge;
        _projections = projections; _documentStore = documentStore; _reporter = reporter;
        _runHistory = runHistory; _versionChanges = versionChanges; _changelog = changelog;
        _tokens = tokens; _aiProvider = aiProvider; _log = log;
    }

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct = default)
    {
        ExecutionId executionId = ExecutionId.New();
        AnalysisExecution execution = AnalysisExecution.Start(executionId, request.Application, request.Mode, DateTimeOffset.UtcNow);
        var stopwatch = Stopwatch.StartNew();
        var sink = new DiscoverySink();
        _log.LogInformation("Execution {ExecutionId} started for {Application} ({Mode})", executionId, request.Application, request.Mode);

        Guid runId = await _runHistory.BeginRunAsync(request.Application.Value, request.Mode.ToString(), execution.StartedAt, ct);
        AiUsage tokensBefore = _tokens.Total;
        int pagesGenerated = 0;

        IReadOnlyList<ApplicationDescriptor> apps = await _registry.GetApplicationsAsync(ct);
        ApplicationDescriptor? descriptor = apps.FirstOrDefault(a => a.Name == request.Application.Value);
        if (descriptor is null)
        {
            sink.Report(Diagnostic.Error($"Application '{request.Application}' is not registered.", PipelineSource));

            return await FinishAsync(execution, request, sink, stopwatch, ExecutionOutcome.Failed, null, ExecutionMetrics.Empty,
                runId, tokensBefore, pagesGenerated, 0, 0, ct);
        }

        Snapshot? previous = await _knowledge.GetSnapshotAsync(request.Application, ct);
        var repositoryIds = descriptor.Repositories.Select(l => new RepositoryId(DeriveRepositoryName(l))).ToList();
        int analyzedArtifacts = 0, prunedArtifacts = 0, discoveredArtifacts = 0;

        // Phase 1: materialize every repository up front. SkipIfUnchanged needs every repo's current
        // commit known before analyzing any of them, so materialization and analysis can't stay interleaved
        // the way they used to. GetLastCommitAsync must run BEFORE RecordRepositoryAsync for this run —
        // otherwise it would see this run's own just-recorded commit and "unchanged" would be trivially true.
        // It also doubles as the diff baseline: passed straight into MaterializeAsync so the source adapter
        // can compute each repository's own changed-file list against it (see GitRepositorySource).
        var materializations = new List<(RepositoryId RepositoryId, string Location, RepositoryMaterialization Materialization, string? PreviousCommit)>();
        foreach ((string location, RepositoryId repositoryId) in descriptor.Repositories.Zip(repositoryIds))
        {
            string? previousCommit = await _runHistory.GetLastCommitAsync(request.Application.Value, location, ct);

            RepositoryMaterialization materialization;
            try { materialization = await _source.MaterializeAsync(repositoryId, location, previousCommit, ct); }
            catch (Exception ex) { sink.Report(Diagnostic.Error($"Failed to materialize '{location}': {ex.Message}", "sourcing")); continue; }

            await _runHistory.RecordRepositoryAsync(runId, request.Application.Value, repositoryId.Value, location,
                ExtractBranch(location), materialization.SourceKind.ToString(), materialization.Commit.Value, ct);

            materializations.Add((repositoryId, location, materialization, previousCommit));
        }

        // SkipIfUnchanged applies whenever every declared repository materialized successfully and sits at
        // the same commit it was last analyzed at. This is the coarse, commit-level check — it runs before
        // any per-file diffing, so a whole app with nothing to do never pays for it.
        bool skip = descriptor.SkipIfUnchanged
            && materializations.Count == descriptor.Repositories.Count
            && materializations.All(m => m.PreviousCommit is not null && m.PreviousCommit == m.Materialization.Commit.Value);

        if (skip)
        {
            sink.Report(Diagnostic.Info("Skipped: every repository unchanged since its last analyzed commit.", PipelineSource));

            return await FinishAsync(execution, request, sink, stopwatch, ExecutionOutcome.Success, previous?.Id, ExecutionMetrics.Empty,
                runId, tokensBefore, pagesGenerated, previous?.Nodes.Count ?? 0, previous?.Relationships.Count ?? 0, ct, historyStatus: "Skipped");
        }

        // Auto-diff: the changed-file set is built entirely from each repository's own auto-computed diff
        // against its last-analyzed commit (GitRepositorySource, via MaterializeAsync above) — there is no
        // external trigger that supplies its own changed-file list; every run figures this out for itself.
        // A repository whose diff couldn't be determined — no previous commit on record (never analyzed
        // before), or the diff itself failed (RepositoryMaterialization.ChangedFiles is null even though
        // its commit did change) — is tracked separately in fullyChangedRepos: every artifact under it is
        // treated as touched, since silently under-reporting a real change would corrupt incremental
        // analysis. Once a previous snapshot exists, every batch run is effectively incremental — a
        // repository with no real change simply prunes everything under it, which is the same outcome a
        // dedicated "full run" branch would have produced, just reached through one unified code path.
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullyChangedRepos = new HashSet<RepositoryId>();
        foreach ((RepositoryId repositoryId, string _, RepositoryMaterialization materialization, string? previousCommit) in materializations)
        {
            if (previousCommit is null) { fullyChangedRepos.Add(repositoryId); continue; }
            if (previousCommit == materialization.Commit.Value) continue;
            if (materialization.ChangedFiles is null) { fullyChangedRepos.Add(repositoryId); continue; }
            foreach (string file in materialization.ChangedFiles) changed.Add(Path.GetFileName(file));
        }

        bool incremental = previous is not null;
        var scope = new ExecutionScope(request.Application, repositoryIds, changed.ToList(), request.Mode, previous);

        foreach ((RepositoryId repositoryId, string _, RepositoryMaterialization materialization, string? _) in materializations)
        {
            IReadOnlyList<Artifact> artifacts = await _scanner.DiscoverAsync(repositoryId, materialization.RootPath, ct);
            discoveredArtifacts += artifacts.Count;
            foreach (Artifact artifact in artifacts)
            {
                // Incremental analyzer pruning: skip artifacts with no changed file under them, unless
                // their whole repository is treated as fully changed (see fullyChangedRepos above).
                if (incremental && !fullyChangedRepos.Contains(repositoryId) && !ArtifactTouched(artifact, changed))
                {
                    prunedArtifacts++;
                    continue;
                }
                analyzedArtifacts++;
                await AnalyzeArtifactAsync(artifact, scope, executionId, repositoryId, materialization.Commit, sink, ct);
            }
        }

        // ---- WRITE SIDE (Validation is the only creator of Knowledge) ----
        // 1. Validate analyzer discoveries into canonical nodes + relationships.
        ValidationResult v1 = await _validation.ValidateAsync(sink.Discoveries, null, ct);
        foreach (Diagnostic d in v1.Diagnostics) sink.Report(d);

        // 2. Incremental carry-forward: retain prior knowledge whose evidence was not invalidated.
        List<KnowledgeNode> nodes = MergeNodes(previous, v1.Nodes, changed, incremental);
        var known = nodes.Select(n => n.Identity).ToList();

        // 3. Relationship Resolution — emits Discoveries that pass through the SAME validation gate.
        IReadOnlyList<RelationshipDiscovery> resolved = await _resolution.ResolveAsync(nodes, ct);
        ValidationResult v2 = await _validation.ValidateAsync(resolved.Cast<Discovery>().ToList(), known, ct);
        List<Relationship> relationships = MergeRelationships(previous, v1.Relationships, v2.Relationships, nodes, changed, incremental);

        // 4. Commit a new immutable Snapshot (append-only).
        Snapshot snapshot = await _knowledge.CommitAsync(request.Application, nodes, relationships, ct);

        // 5. Diff against the previous snapshot — drives metrics, the publish-skip decision below, and
        // (when a version is actually published) the "what changed" changelog.
        SnapshotDiff? diff = previous is null ? null : await _knowledge.DiffAsync(previous.Id, snapshot.Id, ct);
        var metrics = new ExecutionMetrics(stopwatch.Elapsed, sink.Discoveries.OfType<NodeDiscovery>().Count() + sink.Discoveries.OfType<RelationshipDiscovery>().Count(),
            nodes.Count + relationships.Count,
            diff is null ? nodes.Count : diff.AddedNodes.Count + diff.RemovedNodes.Count,
            diff is null ? relationships.Count : diff.AddedRelationships.Count + diff.RemovedRelationships.Count);

        // 6. Projection + Publish (documentation from the Knowledge Model only) — skipped outright when a
        // previous version exists and this run's Knowledge Model diff is completely empty (e.g. a
        // comment-only edit, or an incremental run that pruned everything because nothing under it
        // actually changed): there's nothing new to say, so publishing would just be a wasted, zero-value
        // new version (and, once AI-authored, a wasted AI call for the changelog too).
        if (diff is not null && diff.IsEmpty)
        {
            sink.Report(Diagnostic.Info("Publish skipped: Knowledge Model unchanged (no nodes or relationships added/removed).", PipelineSource));
        }
        else
        {
            try
            {
                IReadOnlyList<ProjectionResult> projections = await _projections.RunAsync(snapshot, repositoryIds.Select(r => r.Value).ToList(), ct);
                List<ProjectionArtifact> pages = projections.SelectMany(p => p.Artifacts).ToList();

                // Publish as a new, additive version — never overwrite or clear a prior one (filesystem or
                // Azure Blob — see IDocumentStore). RunAsync always regenerates the complete current page set,
                // but "current" now means "the newest version," not "the only version": older versions stay
                // reachable in the store so the Viewer can offer a version picker.
                await PublishVersionAsync(request.Application.Value, pages, materializations, diff, ct);

                await UpdateApplicationsIndexAsync(request.Application.Value, ct);
                pagesGenerated = pages.Count;
            }
            catch (Exception ex)
            {
                sink.Report(Diagnostic.Warning($"Projection/publish failed: {ex.Message}", "projection"));
            }
        }

        if (discoveredArtifacts == 0)
            sink.Report(Diagnostic.Warning(
                $"No analyzable projects found for '{request.Application}' — the repositories contain no .NET (.csproj) or Angular (angular.json) projects. Other technologies enter as plugins.", "scanner"));

        sink.Report(Diagnostic.Info($"Mode: {(incremental ? "incremental" : "full")}; analyzed {analyzedArtifacts} artifact(s), pruned {prunedArtifacts}.", PipelineSource));

        bool hadErrors = sink.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

        return await FinishAsync(execution, request, sink, stopwatch, hadErrors ? ExecutionOutcome.Partial : ExecutionOutcome.Success, snapshot.Id, metrics,
            runId, tokensBefore, pagesGenerated, nodes.Count, relationships.Count, ct);
    }

    /// <summary>Extracts the optional "#branch" suffix from a repo location, matching apps.yml's convention.</summary>
    private static string? ExtractBranch(string location)
    {
        int hash = location.LastIndexOf('#');

        return hash < 0 ? null : location[(hash + 1)..];
    }

    private async Task AnalyzeArtifactAsync(Artifact artifact, ExecutionScope scope, ExecutionId executionId,
        RepositoryId repositoryId, Commit commit, DiscoverySink sink, CancellationToken ct)
    {
        _log.LogDebug("Analyzing artifact {Artifact} [{Technology}] in {Repository}", artifact.Name, artifact.Technology, repositoryId);
        IReadOnlyList<IPlugin> plugins = _plugins.SelectFor(artifact);
        if (plugins.Count == 0)
        {
            sink.Report(Diagnostic.Warning($"No plugin claims artifact '{artifact.Name}' [{artifact.Technology}].", "plugin-host"));

            return;
        }

        foreach (IPlugin plugin in plugins)
        {
            string language = plugin.Manifest.Language;
            if (!_engines.Supports(language))
            {
                sink.Report(Diagnostic.Warning($"No language engine for '{language}' (plugin {plugin.Manifest.Id}).", EngineHostSource));
                continue;
            }

            ISemanticModel model;
            try { model = await _engines.GetModelAsync(language, artifact.Path, commit.Value, ct); }
            catch (Exception ex) { sink.Report(Diagnostic.Error($"Language engine failed on '{artifact.Name}': {ex.Message}", EngineHostSource)); continue; }

            var context = new AnalysisContext(executionId, scope, artifact, repositoryId, commit, model.Parser, model);
            try { await plugin.AnalyzeAsync(context, sink, ct); }
            catch (Exception ex) { sink.Report(Diagnostic.Error($"Plugin '{plugin.Manifest.Id}' failed on '{artifact.Name}': {ex.Message}", "plugin")); }
        }
    }

    private static bool ArtifactTouched(Artifact artifact, HashSet<string> changedFileNames)
    {
        string dir = Path.GetDirectoryName(artifact.Path) ?? artifact.Path;

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Any(f => f is not null && changedFileNames.Contains(f));
    }

    private static bool Invalidated(IReadOnlyList<Evidence> evidence, HashSet<string> changed) =>
        evidence.Any(e => e.Location is not null && changed.Contains(Path.GetFileName(e.Location.File)));

    private static List<KnowledgeNode> MergeNodes(Snapshot? previous, IReadOnlyList<KnowledgeNode> fresh, HashSet<string> changed, bool incremental)
    {
        if (!incremental || previous is null) return fresh.ToList();
        var byId = new Dictionary<KnowledgeIdentity, KnowledgeNode>();
        foreach (KnowledgeNode n in previous.Nodes)
            if (!Invalidated(n.Evidence, changed)) byId[n.Identity] = n; // carry forward untouched knowledge
        foreach (KnowledgeNode n in fresh) byId[n.Identity] = n;         // freshly analyzed wins

        return byId.Values.ToList();
    }

    private static List<Relationship> MergeRelationships(Snapshot? previous, IReadOnlyList<Relationship> fresh1,
        IReadOnlyList<Relationship> fresh2, IReadOnlyList<KnowledgeNode> nodes, HashSet<string> changed, bool incremental)
    {
        var known = new HashSet<KnowledgeIdentity>(nodes.Select(n => n.Identity));
        string Key(Relationship r) => $"{r.Type.Value}|{r.From.Value}|{r.To.Value}";
        var map = new Dictionary<string, Relationship>();

        if (incremental && previous is not null)
            foreach (Relationship r in previous.Relationships)
                if (!Invalidated(r.Evidence, changed) && known.Contains(r.From) && known.Contains(r.To))
                    map[Key(r)] = r;

        foreach (Relationship r in fresh1.Concat(fresh2))
            if (known.Contains(r.From) && known.Contains(r.To))
                map[Key(r)] = r;

        return map.Values.ToList();
    }

    private async Task<ExecutionResult> FinishAsync(AnalysisExecution execution, ExecutionRequest request, DiscoverySink sink,
        Stopwatch stopwatch, ExecutionOutcome outcome, SnapshotId? snapshot, ExecutionMetrics metrics,
        Guid runId, AiUsage tokensBefore, int pagesGenerated, int knowledgeNodeCount, int relationshipCount, CancellationToken ct,
        string? historyStatus = null)
    {
        stopwatch.Stop();
        ExecutionMetrics finalMetrics = metrics == ExecutionMetrics.Empty
            ? new ExecutionMetrics(stopwatch.Elapsed, sink.Discoveries.Count, 0, 0, 0)
            : metrics with { Duration = stopwatch.Elapsed };

        // Every Diagnostic ever reported during this run — by any analyzer, any plugin, past or future —
        // funnels through here once, so mirroring each one into the structured log (at the matching
        // severity) is a single choke point that covers the whole pipeline without needing a separate
        // logging call at every individual sink.Report(...) site.
        foreach (Diagnostic d in sink.Diagnostics)
        {
            execution.Report(d);
            LogLevel level = d.Severity switch
            {
                DiagnosticSeverity.Error => LogLevel.Error,
                DiagnosticSeverity.Warning => LogLevel.Warning,
                DiagnosticSeverity.Debug => LogLevel.Debug,
                _ => LogLevel.Information
            };
            _log.Log(level, "[{Source}] {Message}", d.Source, d.Message);
        }
        if (outcome == ExecutionOutcome.Failed) execution.Fail(finalMetrics, DateTimeOffset.UtcNow);
        else execution.Complete(outcome, snapshot ?? SnapshotId.New(), finalMetrics, DateTimeOffset.UtcNow);

        var result = new ExecutionResult(execution.Id, request.Application, outcome, sink.Discoveries, sink.Diagnostics, finalMetrics, snapshot);
        await _executionStore.SaveAsync(result, ct);
        await _reporter.ReportAsync(result, ct);

        // Run History is best-effort observability, not a Knowledge write gate — never let a persistence
        // hiccup here fail an otherwise-successful run.
        try
        {
            AiUsage delta = _tokens.Total;
            AiUsage used = new(delta.PromptTokens - tokensBefore.PromptTokens, delta.CompletionTokens - tokensBefore.CompletionTokens);
            string status = historyStatus ?? outcome.ToString();
            // NoOpAiProvider (no key configured) doesn't implement this — GetType().Name is a fine fallback
            // for that case, since there's only ever one such provider and no ambiguity to resolve.
            var descriptor = _aiProvider as IAiProviderDescriptor;
            string providerName = descriptor?.ProviderName ?? _aiProvider.GetType().Name;
            string? model = descriptor?.Model;
            await _runHistory.CompleteRunAsync(runId, status, DateTimeOffset.UtcNow, providerName, model,
                used.PromptTokens, used.CompletionTokens, pagesGenerated, knowledgeNodeCount, relationshipCount, ct);
        }
        catch (Exception ex)
        {
            // execution has already transitioned out of Running (Report() would throw) — this is
            // observability-only; it must never affect the returned result.
            _log.LogError(ex, "Run History persistence failed for execution {ExecutionId}", execution.Id);
        }

        LogLevel outcomeLevel = outcome switch
        {
            ExecutionOutcome.Failed => LogLevel.Error,
            ExecutionOutcome.Partial => LogLevel.Warning,
            _ => LogLevel.Information
        };
        _log.Log(outcomeLevel, "Execution {ExecutionId} finished for {Application}: {Outcome} in {DurationMs}ms " +
            "({DiscoveriesAccepted} discoveries accepted, {NodesChanged} nodes / {RelationshipsChanged} relationships changed)",
            execution.Id, request.Application, outcome, finalMetrics.Duration.TotalMilliseconds,
            finalMetrics.DiscoveriesAccepted, finalMetrics.NodesChanged, finalMetrics.RelationshipsChanged);

        return result;
    }

    /// <summary>
    /// Publishes one new documentation version for an application under the store's <c>v{N}/</c> prefix —
    /// additive only, so every prior version stays reachable. Returns the new version number.
    /// </summary>
    private async Task<int> PublishVersionAsync(string application, IReadOnlyList<ProjectionArtifact> pages,
        List<(RepositoryId RepositoryId, string Location, RepositoryMaterialization Materialization, string? PreviousCommit)> materializations,
        SnapshotDiff? diff, CancellationToken ct)
    {
        DocumentVersionsIndex existing = await ReadVersionsIndexAsync(application, ct);
        int previousVersion = existing.Versions.Count == 0 ? 0 : existing.Versions.Max(v => v.Number);
        int nextVersion = previousVersion + 1;
        string prefix = $"v{nextVersion}/";

        foreach (ProjectionArtifact page in pages)
            await _documentStore.WriteAsync(application, prefix + page.Name, page.Content, page.ContentType, ct);

        var manifest = new DocumentManifest(pages.Select(p => new DocumentManifestEntry(p.Name, p.Order, p.AiWritten)).ToList());
        await _documentStore.WriteAsync(application, prefix + DocumentManifest.FileName,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions), "application/json", ct);

        var repoCommits = materializations
            .Select(m => new VersionedRepositoryCommit(m.RepositoryId.Value, m.Materialization.Commit.Value)).ToList();
        var updated = new DocumentVersionsIndex(existing.Versions
            .Append(new DocumentVersionEntry(nextVersion, DateTimeOffset.UtcNow, repoCommits, pages.Count)).ToList());
        await _documentStore.WriteAsync(application, DocumentVersionsIndex.FileName,
            JsonSerializer.Serialize(updated, ManifestJsonOptions), "application/json", ct);

        // "What changed" record — only when there's a real previous version to compare against (v1 never
        // gets one; diff is only non-null once a previous Knowledge snapshot existed to diff against).
        if (diff is not null && previousVersion > 0)
            await RecordVersionChangeAsync(application, nextVersion, previousVersion, materializations, diff, ct);

        return nextVersion;
    }

    private async Task RecordVersionChangeAsync(string application, int versionNumber, int previousVersionNumber,
        List<(RepositoryId RepositoryId, string Location, RepositoryMaterialization Materialization, string? PreviousCommit)> materializations,
        SnapshotDiff diff, CancellationToken ct)
    {
        IReadOnlyList<string> repos = materializations.Select(m => m.RepositoryId.Value).ToList();
        (string summary, bool aiWritten) = await _changelog.GenerateAsync(application, repos, diff, ct);

        var repoCommits = materializations
            .Select(m => new RepositoryCommitChange(m.RepositoryId.Value, m.PreviousCommit, m.Materialization.Commit.Value)).ToList();

        var change = new DocumentVersionChange(
            application, versionNumber, previousVersionNumber,
            diff.AddedNodes.Count, diff.RemovedNodes.Count, diff.AddedRelationships.Count, diff.RemovedRelationships.Count,
            diff.AddedNodes.Select(NodeLabel).ToList(), diff.RemovedNodes.Select(NodeLabel).ToList(),
            diff.AddedRelationships.Select(RelationshipLabel).ToList(), diff.RemovedRelationships.Select(RelationshipLabel).ToList(),
            repoCommits, summary, aiWritten, DateTimeOffset.UtcNow);

        await _versionChanges.RecordAsync(change, ct);
    }

    private static string NodeLabel(KnowledgeNode n) => n.Label();

    private static string RelationshipLabel(Relationship r) => r.Label();

    private async Task<DocumentVersionsIndex> ReadVersionsIndexAsync(string application, CancellationToken ct)
    {
        string? json = await _documentStore.ReadAsync(application, DocumentVersionsIndex.FileName, ct);
        if (json is null) return new DocumentVersionsIndex(new List<DocumentVersionEntry>());
        try { return JsonSerializer.Deserialize<DocumentVersionsIndex>(json, ManifestJsonOptions) ?? new DocumentVersionsIndex(new List<DocumentVersionEntry>()); }
        catch (JsonException) { return new DocumentVersionsIndex(new List<DocumentVersionEntry>()); }
    }

    // Adds/updates this application's entry in the shared applications index (a reserved pseudo-
    // application, "_index" — see DocumentManifest/ApplicationsIndex), so a Document Viewer landing page
    // can list every documented app without enumerating the whole store. Batch/CI-CD runs are sequential
    // today, so a plain read-modify-write is safe; concurrent writers would need optimistic concurrency.
    private async Task UpdateApplicationsIndexAsync(string applicationName, CancellationToken ct)
    {
        string? existingJson = await _documentStore.ReadAsync(ApplicationsIndex.IndexApplication, ApplicationsIndex.FileName, ct);
        List<ApplicationIndexEntry> entries = new();
        if (existingJson is not null)
        {
            try { entries = JsonSerializer.Deserialize<ApplicationsIndex>(existingJson, ManifestJsonOptions)?.Applications.ToList() ?? new(); }
            catch (JsonException) { entries = new(); }
        }

        entries.RemoveAll(e => e.Name == applicationName);
        entries.Add(new ApplicationIndexEntry(applicationName, DocumentPaths.SlugifyApplication(applicationName)));

        var index = new ApplicationsIndex(entries.OrderBy(e => e.Name).ToList());
        await _documentStore.WriteAsync(ApplicationsIndex.IndexApplication, ApplicationsIndex.FileName,
            JsonSerializer.Serialize(index, ManifestJsonOptions), "application/json", ct);
    }

    private static string DeriveRepositoryName(string location)
    {
        int hash = location.LastIndexOf('#');
        if (hash >= 0) location = location[..hash];       // drop any "#branch" suffix
        string trimmed = location.TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        string name = Path.GetFileName(trimmed);

        return string.IsNullOrWhiteSpace(name) ? "repo" : name;
    }
}

public static class AnalysisModule
{
    public static IServiceCollection AddAipAnalysis(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageEngineHost, LanguageEngineHost>();
        services.AddSingleton<IPluginHost, PluginHost>();
        services.AddSingleton<IAnalysisPipeline, ExecutionPipeline>();

        return services;
    }
}
