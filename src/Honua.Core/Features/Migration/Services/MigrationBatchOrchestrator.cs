// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Issue #1253. Default <see cref="IMigrationBatchOrchestrator"/> implementation.
/// Composes the existing per-layer Geoservices import pipeline into an ordered,
/// resumable batch run with aggregated progress and post-publish relationship
/// application (issue #1256).
/// </summary>
/// <remarks>
/// The orchestrator resolves scoped collaborators (<see cref="IMigrationBatchRunCatalog"/>,
/// <see cref="IDistributedImportJobManager"/>, <see cref="IGeoservicesImportService"/>,
/// <see cref="IMetadataV2GraphStore"/>) through an <see cref="IServiceScopeFactory"/>
/// per call so it can run from both request handlers and the long-lived batch
/// background service without capturing scoped lifetimes.
/// </remarks>
public sealed partial class MigrationBatchOrchestrator : IMigrationBatchOrchestrator
{
    private static readonly TimeSpan ChildJobTtl = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MigrationBatchOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationBatchOrchestrator"/> class.
    /// </summary>
    /// <param name="scopeFactory">Factory used to resolve scoped collaborators per call.</param>
    /// <param name="logger">Structured logger.</param>
    public MigrationBatchOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<MigrationBatchOrchestrator> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<MigrationBatchRunRecord> StartAsync(
        MigrationBatchStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKind);
        if (request.Layers is null || request.Layers.Count == 0)
        {
            throw new ArgumentException("A batch footprint must contain at least one layer.", nameof(request));
        }

        var batchId = Guid.NewGuid().ToString("N")[..16];
        var ordered = OrderLayers(request.Layers);
        var now = DateTimeOffset.UtcNow;

        var children = new List<MigrationBatchChildRecord>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var spec = ordered[i];
            children.Add(new MigrationBatchChildRecord
            {
                BatchId = batchId,
                Ordinal = i,
                SourceResourceId = spec.SourceResourceId,
                ServiceUrl = spec.ServiceUrl,
                SourceLayerId = spec.LayerId,
                TableName = spec.TableName,
                TargetSchema = spec.TargetSchema,
                ServiceName = spec.ServiceName,
                DependsOn = spec.DependsOn,
                Status = MigrationBatchChildStatus.Pending,
                UpdatedAt = now
            });
        }

        var applyRelationships = request.ApplyRelationships && !string.IsNullOrWhiteSpace(request.ManifestBody);
        var record = new MigrationBatchRunRecord
        {
            BatchId = batchId,
            SourceKind = request.SourceKind,
            SourceUrl = request.SourceUrl,
            SourceDisplayName = request.SourceDisplayName,
            Status = MigrationBatchRunStatus.Running,
            StartedAt = now,
            TotalChildren = children.Count,
            ApplyRelationships = applyRelationships
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IMigrationBatchRunCatalog>();
        var created = await catalog.CreateAsync(
            record,
            applyRelationships ? request.ManifestBody : null,
            children,
            cancellationToken).ConfigureAwait(false);

        Log.BatchStarted(_logger, created.BatchId, created.SourceKind, created.TotalChildren, applyRelationships);

        // Record the run in the operator-facing run catalog and persist the manifest as
        // ArcGIS migration evidence (#1598) so the admin run-list and evidence endpoints
        // surface real data for this batch. Evidence persistence is best-effort and must
        // never fail the batch itself.
        await RecordRunStartedAsync(scope.ServiceProvider, created, cancellationToken).ConfigureAwait(false);
        await SaveArcGisManifestEvidenceAsync(scope.ServiceProvider, created, request.ManifestBody, cancellationToken).ConfigureAwait(false);

        // Queue the first ready children immediately so callers see progress
        // without waiting for the background advance tick.
        var advanced = await AdvanceCoreAsync(scope.ServiceProvider, created.BatchId, cancellationToken).ConfigureAwait(false);
        return advanced ?? created;
    }

    /// <inheritdoc />
    public async Task<MigrationBatchRunRecord?> AdvanceAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await AdvanceCoreAsync(scope.ServiceProvider, batchId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MigrationBatchRunRecord?> AdvanceCoreAsync(
        IServiceProvider services,
        string batchId,
        CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<IMigrationBatchRunCatalog>();
        var jobManager = services.GetRequiredService<IDistributedImportJobManager>();

        var batch = await catalog.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        if (batch.Status is MigrationBatchRunStatus.Succeeded
            or MigrationBatchRunStatus.Failed
            or MigrationBatchRunStatus.Cancelled)
        {
            return batch;
        }

        var children = (await catalog.GetChildrenAsync(batchId, cancellationToken).ConfigureAwait(false))
            .OrderBy(static c => c.Ordinal)
            .ToList();

        // 1. Reconcile running children against their per-layer import jobs.
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Status != MigrationBatchChildStatus.Running || string.IsNullOrWhiteSpace(child.JobId))
            {
                continue;
            }

            var progress = await jobManager.ProgressStore.GetProgressAsync(child.JobId!, cancellationToken).ConfigureAwait(false);
            var mapped = MapChildStatus(progress?.Status);
            if (mapped is null)
            {
                continue; // still in flight
            }

            var updated = await catalog.UpdateChildAsync(
                batchId,
                child.Ordinal,
                mapped.Value,
                child.JobId,
                progress?.PublishedLayerId,
                progress?.ErrorMessage,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            if (updated is not null)
            {
                children[i] = updated;
            }
        }

        // 2. Queue the next ready children (dependencies satisfied, no blocking failure).
        var succeededIds = children
            .Where(static c => c.Status == MigrationBatchChildStatus.Succeeded)
            .Select(static c => c.SourceResourceId)
            .ToHashSet(StringComparer.Ordinal);
        var hasBlockingFailure = children.Any(static c =>
            c.Status is MigrationBatchChildStatus.Failed or MigrationBatchChildStatus.Cancelled);
        var hasRunning = children.Any(static c => c.Status == MigrationBatchChildStatus.Running);

        if (!hasBlockingFailure && !hasRunning)
        {
            // Sequential execution: queue the first pending child whose dependencies
            // are all satisfied. One child in flight at a time keeps the batch
            // deterministic and bounds load on the source service.
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child.Status != MigrationBatchChildStatus.Pending)
                {
                    continue;
                }

                if (!child.DependsOn.All(succeededIds.Contains))
                {
                    continue;
                }

                var jobId = await QueueChildAsync(jobManager, child, cancellationToken).ConfigureAwait(false);
                var queued = await catalog.UpdateChildAsync(
                    batchId,
                    child.Ordinal,
                    MigrationBatchChildStatus.Running,
                    jobId,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                if (queued is not null)
                {
                    children[i] = queued;
                }

                Log.ChildQueued(_logger, batchId, child.Ordinal, child.SourceResourceId, jobId);
                break;
            }
        }

        // 3. Roll up counts and batch status.
        return await RollUpAsync(services, catalog, batch, children, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MigrationBatchRunRecord?> RollUpAsync(
        IServiceProvider services,
        IMigrationBatchRunCatalog catalog,
        MigrationBatchRunRecord batch,
        List<MigrationBatchChildRecord> children,
        CancellationToken cancellationToken)
    {
        var succeeded = children.Count(static c => c.Status == MigrationBatchChildStatus.Succeeded);
        var failed = children.Count(static c => c.Status == MigrationBatchChildStatus.Failed);
        var cancelled = children.Count(static c => c.Status == MigrationBatchChildStatus.Cancelled);
        var needsReview = children.Count(static c => c.Status == MigrationBatchChildStatus.NeedsReview);
        var running = children.Count(static c => c.Status == MigrationBatchChildStatus.Running);
        var pending = children.Count(static c => c.Status == MigrationBatchChildStatus.Pending);
        var hasBlockingFailure = failed > 0 || cancelled > 0;

        // The batch is terminal when nothing can advance further: either every
        // child reached a terminal state, or a blocking failure/cancellation has
        // stranded the remaining pending children behind the sequential gate.
        // Leaving the failed child + its pending successors in place keeps the
        // batch resumable (restart re-runs the failed layer, skips succeeded).
        var isTerminal = (running == 0 && pending == 0) || (hasBlockingFailure && running == 0);

        var status = MigrationBatchRunStatus.Running;
        DateTimeOffset? completedAt = null;
        var relationshipsApplied = batch.RelationshipsApplied;
        string? note = batch.StatusNote;

        if (isTerminal)
        {
            if (failed > 0)
            {
                status = MigrationBatchRunStatus.Failed;
            }
            else if (cancelled > 0)
            {
                status = MigrationBatchRunStatus.Cancelled;
            }
            else if (needsReview > 0)
            {
                status = MigrationBatchRunStatus.NeedsReview;
            }
            else
            {
                status = MigrationBatchRunStatus.Succeeded;
            }

            // Apply relationships only when every child published cleanly and the
            // batch asked for it (issue #1256). NeedsReview children still published
            // data, so relationship-apply is eligible; hard failures are not.
            if (batch.ApplyRelationships
                && !relationshipsApplied
                && !hasBlockingFailure
                && pending == 0)
            {
                note = await ApplyRelationshipsAsync(services, batch, children, cancellationToken).ConfigureAwait(false);
                relationshipsApplied = true;
            }

            completedAt = DateTimeOffset.UtcNow;
        }

        var updated = await catalog.UpdateBatchAsync(
            batch.BatchId,
            status,
            succeeded,
            failed,
            cancelled,
            completedAt,
            relationshipsApplied != batch.RelationshipsApplied ? relationshipsApplied : null,
            note,
            cancellationToken).ConfigureAwait(false);

        if (isTerminal && completedAt is not null)
        {
            await RecordRunCompletedAsync(
                services,
                batch.BatchId,
                status,
                completedAt.Value,
                BuildCompletionNote(children.Count, succeeded, failed, cancelled, needsReview, note),
                cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>
    /// Best-effort write of the operator-facing <see cref="MigrationRunRecord"/> for a
    /// freshly started batch (#1598). Failures are logged and swallowed so evidence
    /// bookkeeping never blocks the migration itself.
    /// </summary>
    private async Task RecordRunStartedAsync(
        IServiceProvider services,
        MigrationBatchRunRecord batch,
        CancellationToken cancellationToken)
    {
        var runCatalog = services.GetService<IMigrationRunCatalog>();
        if (runCatalog is null)
        {
            return;
        }

        try
        {
            await runCatalog.RecordStartedAsync(
                new MigrationRunRecord
                {
                    RunId = batch.BatchId,
                    SourceKind = batch.SourceKind,
                    SourceUrl = RedactUrl(batch.SourceUrl),
                    SourceDisplayName = batch.SourceDisplayName,
                    Status = MigrationRunStatus.Running,
                    StartedAt = batch.StartedAt
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RunRecordWriteFailed(_logger, batch.BatchId, ex);
        }
    }

    /// <summary>
    /// Best-effort terminal-state update of the run record once the batch rolls up to a
    /// terminal status (#1598). <see cref="MigrationBatchRunStatus.NeedsReview"/> maps to
    /// <see cref="MigrationRunStatus.Succeeded"/> — the data published — with the review
    /// posture preserved in the status note.
    /// </summary>
    private async Task RecordRunCompletedAsync(
        IServiceProvider services,
        string batchId,
        MigrationBatchRunStatus batchStatus,
        DateTimeOffset completedAt,
        string statusNote,
        CancellationToken cancellationToken)
    {
        var runCatalog = services.GetService<IMigrationRunCatalog>();
        if (runCatalog is null)
        {
            return;
        }

        var runStatus = batchStatus switch
        {
            MigrationBatchRunStatus.Failed => MigrationRunStatus.Failed,
            MigrationBatchRunStatus.Cancelled => MigrationRunStatus.Cancelled,
            _ => MigrationRunStatus.Succeeded
        };

        try
        {
            await runCatalog.RecordCompletedAsync(
                batchId,
                runStatus,
                completedAt,
                evidencePackRef: null,
                evidencePackFingerprint: null,
                evidencePackBody: null,
                statusNote,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RunRecordWriteFailed(_logger, batchId, ex);
        }
    }

    /// <summary>
    /// Best-effort persistence of the batch manifest as ArcGIS migration evidence (#1598)
    /// so <c>GET /api/v1/admin/import/arcgis/migrations</c> lists the run. A missing or
    /// unparseable manifest skips the write without failing the batch.
    /// </summary>
    private async Task SaveArcGisManifestEvidenceAsync(
        IServiceProvider services,
        MigrationBatchRunRecord batch,
        string? manifestBody,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestBody))
        {
            return;
        }

        var evidenceStore = services.GetService<IArcGisMigrationEvidenceStore>();
        if (evidenceStore is null)
        {
            return;
        }

        MigrationManifestArtifact? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBody, MigrationEvidencePackJsonContext.Default.MigrationManifestArtifact);
        }
        catch (JsonException ex)
        {
            Log.EvidenceManifestInvalid(_logger, batch.BatchId, ex);
            return;
        }

        if (manifest is null)
        {
            return;
        }

        var sourceUrl = RedactUrl(string.IsNullOrWhiteSpace(manifest.Source.BaseUrl) ? batch.SourceUrl : manifest.Source.BaseUrl);
        try
        {
            await evidenceStore.SaveManifestAsync(
                new ArcGisMigrationRunRecord
                {
                    RunId = batch.BatchId,
                    SourceUrl = sourceUrl,
                    SourceDisplayName = string.IsNullOrWhiteSpace(manifest.Source.DisplayName)
                        ? batch.SourceDisplayName
                        : manifest.Source.DisplayName,
                    SourceVersion = manifest.Source.Version,
                    CreatedAt = batch.StartedAt
                },
                manifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.EvidenceWriteFailed(_logger, batch.BatchId, ex);
        }
    }

    private static string BuildCompletionNote(
        int total,
        int succeeded,
        int failed,
        int cancelled,
        int needsReview,
        string? existingNote)
    {
        var summary = $"{succeeded}/{total} layer(s) succeeded, {failed} failed, {cancelled} cancelled, {needsReview} need(s) review.";
        return string.IsNullOrWhiteSpace(existingNote) ? summary : $"{summary} {existingNote}";
    }

    /// <summary>
    /// Strips userinfo, query, and fragment from a source URL before persistence, matching
    /// the privacy posture documented on <see cref="MigrationRunRecord.SourceUrl"/>.
    /// </summary>
    private static string RedactUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return sourceUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
    }

    private async Task<string?> ApplyRelationshipsAsync(
        IServiceProvider services,
        MigrationBatchRunRecord batch,
        IReadOnlyList<MigrationBatchChildRecord> children,
        CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<IMigrationBatchRunCatalog>();
        var manifestBody = await catalog.GetManifestBodyAsync(batch.BatchId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(manifestBody))
        {
            return batch.StatusNote;
        }

        MigrationManifestArtifact? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBody, MigrationEvidencePackJsonContext.Default.MigrationManifestArtifact);
        }
        catch (JsonException ex)
        {
            Log.RelationshipManifestInvalid(_logger, batch.BatchId, ex);
            return "Relationship-apply skipped: batch manifest could not be parsed.";
        }

        if (manifest is null)
        {
            return "Relationship-apply skipped: batch manifest was empty.";
        }

        var publishedLayerMap = children
            .Where(static c => c.PublishedLayerId.HasValue)
            .GroupBy(static c => c.SourceResourceId, StringComparer.Ordinal)
            .ToDictionary(
                static g => g.Key,
                static g => g.First().PublishedLayerId!.Value,
                StringComparer.Ordinal);

        var importService = services.GetRequiredService<IGeoservicesImportService>();
        var graphStore = services.GetService<IMetadataV2GraphStore>();

        var outcomes = await importService.ApplyRelationshipsAsync(
            manifest,
            publishedLayerMap,
            graphStore,
            cancellationToken).ConfigureAwait(false);

        var applied = outcomes.Count(static o => o.Outcome == MigrationCatalogWriteOutcome.Created);
        Log.RelationshipsApplied(_logger, batch.BatchId, outcomes.Length, applied);
        return $"Relationship-apply complete: {applied} created, {outcomes.Length - applied} skipped/existing across {outcomes.Length} relationship(s).";
    }

    private static async Task<string> QueueChildAsync(
        IDistributedImportJobManager jobManager,
        MigrationBatchChildRecord child,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var importRequest = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = child.ServiceUrl,
            LayerId = child.SourceLayerId,
            TableName = child.TableName,
            TargetSchema = child.TargetSchema,
            AutoPublish = true,
            ServiceName = child.ServiceName
        };

        var progress = GeoservicesImportProgress.CreateInitial(
            jobId,
            child.ServiceUrl,
            child.SourceLayerId,
            child.TableName) with
        {
            ServiceName = child.ServiceName
        };

        await jobManager.RequestStore.SetProgressAsync(jobId, importRequest, ChildJobTtl, cancellationToken).ConfigureAwait(false);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, ChildJobTtl, cancellationToken).ConfigureAwait(false);
        await jobManager.JobQueue.EnqueueAsync(jobId, cancellationToken).ConfigureAwait(false);
        return jobId;
    }

    private static MigrationBatchChildStatus? MapChildStatus(GeoservicesImportStatus? status) => status switch
    {
        null => null,
        GeoservicesImportStatus.Completed => MigrationBatchChildStatus.Succeeded,
        GeoservicesImportStatus.Failed => MigrationBatchChildStatus.Failed,
        GeoservicesImportStatus.Cancelled => MigrationBatchChildStatus.Cancelled,
        GeoservicesImportStatus.NeedsReview => MigrationBatchChildStatus.NeedsReview,
        _ => null // still in flight
    };

    /// <summary>
    /// Deterministic topological order over the footprint: dependencies first,
    /// stable by the supplied order for ties. Cycles and dangling dependency
    /// references fall back to the supplied order so a malformed footprint still
    /// runs rather than deadlocking.
    /// </summary>
    private static List<MigrationBatchLayerSpec> OrderLayers(IReadOnlyList<MigrationBatchLayerSpec> layers)
    {
        var byId = new Dictionary<string, MigrationBatchLayerSpec>(StringComparer.Ordinal);
        var inputIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < layers.Count; i++)
        {
            // Last write wins on duplicate ids; index preserves first appearance.
            byId[layers[i].SourceResourceId] = layers[i];
            inputIndex.TryAdd(layers[i].SourceResourceId, i);
        }

        var ordered = new List<MigrationBatchLayerSpec>(layers.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);

        // Visit in stable input order so the output is deterministic.
        foreach (var spec in layers.OrderBy(s => inputIndex[s.SourceResourceId]))
        {
            Visit(spec.SourceResourceId, byId, inputIndex, visited, onStack, ordered);
        }

        return ordered;
    }

    private static void Visit(
        string id,
        IReadOnlyDictionary<string, MigrationBatchLayerSpec> byId,
        IReadOnlyDictionary<string, int> inputIndex,
        HashSet<string> visited,
        HashSet<string> onStack,
        List<MigrationBatchLayerSpec> ordered)
    {
        if (visited.Contains(id) || !byId.TryGetValue(id, out var spec))
        {
            return;
        }

        if (!onStack.Add(id))
        {
            // Cycle: stop recursing; the node will be emitted when the stack unwinds.
            return;
        }

        foreach (var dep in spec.DependsOn.Where(byId.ContainsKey).OrderBy(d => inputIndex[d]))
        {
            Visit(dep, byId, inputIndex, visited, onStack, ordered);
        }

        onStack.Remove(id);
        if (visited.Add(id))
        {
            ordered.Add(spec);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7980, LogLevel.Information,
            "Migration batch {BatchId} started (source={SourceKind}, children={ChildCount}, applyRelationships={ApplyRelationships})")]
        public static partial void BatchStarted(ILogger logger, string batchId, string sourceKind, int childCount, bool applyRelationships);

        [LoggerMessage(7981, LogLevel.Information,
            "Migration batch {BatchId} queued child {Ordinal} ({SourceResourceId}) as import job {JobId}")]
        public static partial void ChildQueued(ILogger logger, string batchId, int ordinal, string sourceResourceId, string jobId);

        [LoggerMessage(7982, LogLevel.Information,
            "Migration batch {BatchId} applied relationships: {OutcomeCount} outcome(s), {AppliedCount} created")]
        public static partial void RelationshipsApplied(ILogger logger, string batchId, int outcomeCount, int appliedCount);

        [LoggerMessage(7983, LogLevel.Warning,
            "Migration batch {BatchId} manifest could not be parsed for relationship-apply")]
        public static partial void RelationshipManifestInvalid(ILogger logger, string batchId, Exception exception);

        [LoggerMessage(7984, LogLevel.Warning,
            "Migration batch {BatchId} failed to write its migration-run record; admin run list may be incomplete")]
        public static partial void RunRecordWriteFailed(ILogger logger, string batchId, Exception exception);

        [LoggerMessage(7985, LogLevel.Warning,
            "Migration batch {BatchId} failed to persist ArcGIS migration evidence; evidence endpoints may be incomplete")]
        public static partial void EvidenceWriteFailed(ILogger logger, string batchId, Exception exception);

        [LoggerMessage(7986, LogLevel.Warning,
            "Migration batch {BatchId} manifest could not be parsed for evidence persistence")]
        public static partial void EvidenceManifestInvalid(ILogger logger, string batchId, Exception exception);
    }
}
