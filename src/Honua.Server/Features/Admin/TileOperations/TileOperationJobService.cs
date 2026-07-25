// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Admin.TileOperations;

internal interface ITileOperationJobService
{
    Task<string> StartAsync(TileOperationStartRequest request, string? schemaName = null, CancellationToken cancellationToken = default);
    Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);
    Task<string?> RetryAsync(string jobId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> ReadQueuedJobIdsAsync(CancellationToken cancellationToken = default);
    Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default);
}

internal sealed partial class TileOperationJobService(
    IUniversalProgressStore progressStore,
    IDistributedCache? requestCache,
    OutputCacheInvalidationService cacheInvalidationService,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<TileOptions> tileOptions,
    IOptions<LimitsOptions> limitsOptions,
    ILogger<TileOperationJobService> logger,
    IConnectionMultiplexer? redis = null,
    ITileCacheGenerationCheckpointStore? checkpointStore = null) : ITileOperationJobService
{
    private const string MissingRequestFailureMessage = "Tile operation request metadata is no longer available.";
    private readonly IUniversalProgressStore _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
    private readonly IDistributedCache? _requestCache = requestCache;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
    private readonly ILogger<TileOperationJobService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConnectionMultiplexer? _redis = redis;

    // In-process channel path keeps the legacy 5,000-tile ceiling so a single
    // serving pod is never asked to materialize an unbounded gridset. The
    // batch-dispatched path (TileCacheJobExecutor) lifts this via TileCacheBatchOptions.
    private const int InProcessMaxTilesCeiling = 5_000;

    // progressStore/logger are already guaranteed non-null here: the _progressStore and
    // _logger field initializers above run first (declaration order) and would have
    // already thrown if either parameter were null.
    private readonly TileOperationExecutionCore _executionCore = new(
        progressStore,
        cacheInvalidationService ?? throw new ArgumentNullException(nameof(cacheInvalidationService)),
        tileOptions ?? throw new ArgumentNullException(nameof(tileOptions)),
        limitsOptions ?? throw new ArgumentNullException(nameof(limitsOptions)),
        logger,
        InProcessMaxTilesCeiling,
        checkpointStore);

    private const int JobRequestRetentionHours = 24;
    private static readonly TimeSpan _jobRequestRetention = TimeSpan.FromHours(JobRequestRetentionHours);
    private const string RequestCacheKeyPrefix = "tile:request:";
    private const string ClaimKeyPrefix = "tile:job:claim:";
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CachedTileOperationRequest> _jobRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTokens = new(StringComparer.Ordinal);
    private readonly Channel<string> _jobQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public async Task<string> StartAsync(TileOperationStartRequest request, string? schemaName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PruneExpiredJobRequests();

        var normalized = NormalizeRequest(request);
        var jobId = Guid.NewGuid().ToString("N");

        // Stamp a stable generation id on the first submission so seed/warm generations are
        // resumable (issue #2661). A retry (RetryAsync) forwards the original request unchanged,
        // preserving this id across a new job id so the retry resumes the SAME generation rather
        // than forking a fresh full-grid pass.
        if (string.IsNullOrWhiteSpace(normalized.GenerationId))
        {
            normalized = normalized with { GenerationId = jobId };
        }

        _jobRequests[jobId] = CreateCachedRequest(normalized, schemaName);
        await PersistJobRequestAsync(jobId, normalized, schemaName, cancellationToken).ConfigureAwait(false);

        var progress = TileOperationProgress.CreateInitial(
            jobId,
            normalized.Operation,
            normalized.ServiceId,
            normalized.LayerId,
            normalized.TileMatrixSetId);
        await _progressStore.SetProgressAsync(jobId, progress, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
        await _jobQueue.Writer.WriteAsync(jobId, cancellationToken).ConfigureAwait(false);

        TileOperationMetrics.QueueDepth.Add(1, new TagList { { "operation", normalized.Operation } });
        return jobId;
    }

    public Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var tileCacheIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.TileCache, cancellationToken).ConfigureAwait(false);
        var archiveIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.PMTilesArchive, cancellationToken).ConfigureAwait(false);
        var publishIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.PMTilesPublish, cancellationToken).ConfigureAwait(false);
        var operationIds = tileCacheIds.Concat(archiveIds).Concat(publishIds).Distinct().ToList();
        var result = new List<TileOperationProgress>(operationIds.Count);
        foreach (var operationId in operationIds)
        {
            var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(operationId, cancellationToken).ConfigureAwait(false);
            if (progress == null)
            {
                continue;
            }

            if (activeOnly &&
                progress.Status is not (OperationStatus.Queued or OperationStatus.Processing))
            {
                continue;
            }

            result.Add(progress);
        }

        return result
            .OrderByDescending(static item => item.StartedAt)
            .ToArray();
    }

    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken).ConfigureAwait(false);
        if (progress == null)
        {
            return false;
        }

        if (progress.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
        {
            return false;
        }

        if (_runningTokens.TryGetValue(jobId, out var tokenSource))
        {
            try
            {
                tokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The worker finished and disposed its linked CTS between our read of
                // _runningTokens and the Cancel() call. The job is already done — treat
                // the signal as a no-op rather than failing the cancel request.
            }
        }

        var cancelled = (TileOperationProgress)progress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled by user");
        await _progressStore.SetProgressAsync(jobId, cancelled, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
        RefreshJobRequestRetention(jobId);
        return true;
    }

    public async Task<string?> RetryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var originalRequest = await TryGetActiveJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (originalRequest == null)
        {
            return null;
        }

        var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken).ConfigureAwait(false);
        if (progress == null ||
            progress.Status is not (OperationStatus.Failed or OperationStatus.Cancelled))
        {
            return null;
        }

        _jobRequests.TryRemove(jobId, out _);
        return await StartAsync(originalRequest.Value.Request, originalRequest.Value.SchemaName, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadQueuedJobIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recoveredJobIds = await RecoverQueuedJobIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var jobId in recoveredJobIds)
        {
            yield return jobId;
        }

        await foreach (var jobId in _jobQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return jobId;
        }
    }

    public async Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var leaseCoordinator = await TryAcquireJobLeaseAsync(jobId).ConfigureAwait(false);
        if (_redis != null && leaseCoordinator == null)
        {
            return;
        }

        using var renewalCts = leaseCoordinator is null ? null : new CancellationTokenSource();
        using var processingCts = leaseCoordinator is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseCoordinator.LeaseLostToken);
        var renewalTask = leaseCoordinator is null
            ? Task.CompletedTask
            : RenewLeaseAsync(leaseCoordinator, renewalCts!.Token);
        var processingToken = processingCts?.Token ?? cancellationToken;

        // Outer try/finally: any post-acquire exit (early return, throw, cancel)
        // must release the Redis claim and dispose the coordinator. Releasing only
        // inside the inner processing finally would leak the lease for missing-
        // request, already-cancelled, or pre-dispatch lookup-failure exits.
        try
        {
            var cachedRequest = await TryGetActiveJobRequestAsync(jobId, processingToken).ConfigureAwait(false);
            if (cachedRequest == null)
            {
                var missingRequestProgress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken).ConfigureAwait(false);
                await MarkMissingRequestAsync(jobId, missingRequestProgress, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var request = cachedRequest.Value.Request;

            TileOperationMetrics.QueueDepth.Add(-1, new TagList { { "operation", request.Operation } });

            var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken).ConfigureAwait(false)
                ?? TileOperationProgress.CreateInitial(
                    jobId,
                    request.Operation,
                    request.ServiceId,
                    request.LayerId,
                    request.TileMatrixSetId);

            if (progress.Status == OperationStatus.Cancelled)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            using var linkedCts = processingCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(processingToken);
            _runningTokens[jobId] = linkedCts;
            var shouldRequeue = false;

            var started = progress with
            {
                Status = OperationStatus.Processing,
                CurrentPhase = $"Running {request.Operation} operation"
            };
            await _progressStore.SetProgressAsync(jobId, started, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);

            TileOperationProgress finalProgress;
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var schemaContext = scope.ServiceProvider.GetService<SchemaContext>();
                var previousSchema = schemaContext?.CurrentSchema;

                try
                {
                    if (!string.IsNullOrWhiteSpace(cachedRequest.Value.SchemaName) && schemaContext != null)
                    {
                        schemaContext.CurrentSchema = cachedRequest.Value.SchemaName;
                    }

                    finalProgress = await _executionCore.ExecuteAsync(
                        started,
                        request,
                        scope.ServiceProvider,
                        linkedCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (schemaContext != null)
                    {
                        schemaContext.CurrentSchema = previousSchema;
                    }
                }
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                if (leaseCoordinator?.LeaseLostToken.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
                {
                    finalProgress = started with
                    {
                        Status = OperationStatus.Queued,
                        CompletedAt = null,
                        ErrorMessage = null,
                        CurrentPhase = "Lease lost; awaiting retry"
                    };
                    shouldRequeue = true;
                }
                else
                {
                    finalProgress = (TileOperationProgress)started.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");
                }
            }
            // Intentional catch-all: this is a per-job execution boundary inside the
            // background tile-operation processing loop. A single job's failure
            // (any exception from the execution core) must not crash the queue
            // processor; it is recorded as a failed job progress below instead.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogJobFailed(_logger, jobId, request.Operation, ex);
                finalProgress = started with
                {
                    Status = OperationStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Tile operation failed.",
                    CurrentPhase = "Failed"
                };
            }
            finally
            {
                _runningTokens.TryRemove(jobId, out _);
                stopwatch.Stop();
            }

            await _progressStore.SetProgressAsync(jobId, finalProgress, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
            TileOperationMetrics.JobDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new TagList { { "operation", request.Operation } });
            TileOperationMetrics.JobCount.Add(
                1,
                new TagList
                {
                    { "operation", request.Operation },
                    { "status", finalProgress.Status.ToString().ToLowerInvariant() }
                });

            if (finalProgress.Status == OperationStatus.Completed)
            {
                _jobRequests.TryRemove(jobId, out _);
                await RemovePersistedJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                RefreshJobRequestRetention(jobId);
                await PersistJobRequestAsync(jobId, request, cachedRequest.Value.SchemaName, cancellationToken).ConfigureAwait(false);
                if (shouldRequeue)
                {
                    await _jobQueue.Writer.WriteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Not a plain `using`: the coordinator must first have its renewal loop
            // cancelled and awaited, then release its Redis claim asynchronously, and
            // only then be disposed - a `using` declaration can't sequence that
            // async cleanup ahead of disposal.
            if (renewalCts != null)
            {
                renewalCts.Cancel();
                await renewalTask.ConfigureAwait(false);
                await leaseCoordinator!.ReleaseAsync().ConfigureAwait(false);
                DeferredDisposal.Dispose(leaseCoordinator);
            }
        }
    }

    private static TileOperationStartRequest NormalizeRequest(TileOperationStartRequest request)
    {
        var operation = request.Operation.Trim().ToLowerInvariant();
        if (operation is not ("seed" or "warm" or "invalidate" or "purge" or "archive" or "publish"))
        {
            throw new ArgumentException("Operation must be one of: seed, warm, invalidate, purge, archive, publish.", nameof(request));
        }

        return request with
        {
            Operation = operation,
            TileMatrixSetId = string.IsNullOrWhiteSpace(request.TileMatrixSetId)
                ? "WebMercatorQuad"
                : request.TileMatrixSetId.Trim()
        };
    }

    private static CachedTileOperationRequest CreateCachedRequest(TileOperationStartRequest request, string? schemaName)
    {
        return new CachedTileOperationRequest(request, schemaName, DateTimeOffset.UtcNow.Add(_jobRequestRetention));
    }

    private async Task<CachedTileOperationRequest?> TryGetActiveJobRequestAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobRequests.TryGetValue(jobId, out var cachedRequest))
        {
            if (cachedRequest.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cachedRequest;
            }

            _jobRequests.TryRemove(jobId, out _);
        }

        if (_requestCache == null)
        {
            return null;
        }

        var json = await _requestCache.GetStringAsync(GetRequestCacheKey(jobId), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var request = TryDeserializePersistedJobRequest(json);
        if (request == null)
        {
            await RemovePersistedJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var restoredRequest = CreateCachedRequest(request.Request, request.SchemaName);
        _jobRequests[jobId] = restoredRequest;
        return restoredRequest;
    }

    private static PersistedTileOperationRequest? TryDeserializePersistedJobRequest(string json)
    {
        try
        {
            var persistedRequest = JsonSerializer.Deserialize(
                json,
                TileOperationsJsonContext.Default.PersistedTileOperationRequest);
            if (persistedRequest is { Request: not null })
            {
                return persistedRequest;
            }
        }
        catch (JsonException)
        {
            // Older deployments stored TileOperationStartRequest directly in cache.
        }

        try
        {
            var legacyRequest = JsonSerializer.Deserialize(
                json,
                TileOperationsJsonContext.Default.TileOperationStartRequest);
            if (legacyRequest != null)
            {
                return new PersistedTileOperationRequest
                {
                    Request = legacyRequest
                };
            }
        }
        catch (JsonException)
        {
            // Neither the current nor legacy persisted-request shape could be parsed;
            // treat the cached entry as unrecoverable rather than surfacing a parse error.
        }

        return null;
    }

    private void RefreshJobRequestRetention(string jobId)
    {
        if (!_jobRequests.TryGetValue(jobId, out var cachedRequest) ||
            cachedRequest.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return;
        }

        _jobRequests[jobId] = CreateCachedRequest(cachedRequest.Request, cachedRequest.SchemaName);
    }

    private void PruneExpiredJobRequests()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (jobId, request) in _jobRequests)
        {
            if (request.ExpiresAtUtc <= now)
            {
                _jobRequests.TryRemove(jobId, out _);
            }
        }
    }

    private async Task PersistJobRequestAsync(
        string jobId,
        TileOperationStartRequest request,
        string? schemaName,
        CancellationToken cancellationToken)
    {
        if (_requestCache == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            new PersistedTileOperationRequest
            {
                Request = request,
                SchemaName = schemaName
            },
            TileOperationsJsonContext.Default.PersistedTileOperationRequest);
        await _requestCache.SetStringAsync(
                GetRequestCacheKey(jobId),
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _jobRequestRetention },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RemovePersistedJobRequestAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_requestCache == null)
        {
            return;
        }

        await _requestCache.RemoveAsync(GetRequestCacheKey(jobId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> RecoverQueuedJobIdsAsync(CancellationToken cancellationToken)
    {
        var tileCacheIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.TileCache, cancellationToken).ConfigureAwait(false);
        var archiveIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.PMTilesArchive, cancellationToken).ConfigureAwait(false);
        var publishIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.PMTilesPublish, cancellationToken).ConfigureAwait(false);
        var activeOperationIds = tileCacheIds.Concat(archiveIds).Concat(publishIds).Distinct().ToList();
        var recovered = new List<string>(activeOperationIds.Count);

        foreach (var jobId in activeOperationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken).ConfigureAwait(false);
            if (progress == null ||
                progress.Status is not (OperationStatus.Queued or OperationStatus.Processing))
            {
                continue;
            }

            var request = await TryGetActiveJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (request == null)
            {
                await MarkMissingRequestAsync(jobId, progress, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (progress.Status == OperationStatus.Processing)
            {
                if (!await ShouldRecoverProcessingJobAsync(jobId).ConfigureAwait(false))
                {
                    continue;
                }

                progress = progress with
                {
                    Status = OperationStatus.Queued,
                    CurrentPhase = "Recovered after worker restart"
                };
                await _progressStore.SetProgressAsync(jobId, progress, _jobRequestRetention, cancellationToken).ConfigureAwait(false);
            }

            recovered.Add(jobId);
        }

        return recovered;
    }

    private async Task MarkMissingRequestAsync(
        string jobId,
        TileOperationProgress? progress,
        CancellationToken cancellationToken)
    {
        if (progress is not null && progress.Status is (OperationStatus.Queued or OperationStatus.Processing))
        {
            try
            {
                var failed = progress with
                {
                    Status = OperationStatus.Failed,
                    ErrorMessage = MissingRequestFailureMessage,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Tile operation failed"
                };
                await _progressStore.SetProgressAsync(jobId, failed, _jobRequestRetention, cancellationToken).ConfigureAwait(false);
            }
            // Intentional catch-all: this is a best-effort status write for a job
            // whose backing request already disappeared; failing to persist the
            // "missing request" status must not block the caller's cleanup below.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                TileOperationLog.MissingRequestStatusPersistenceFailed(_logger, jobId, ex);
            }
        }

        _jobRequests.TryRemove(jobId, out _);
        await RemovePersistedJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    private static string GetRequestCacheKey(string jobId) => $"{RequestCacheKeyPrefix}{jobId}";

    private async Task<bool> ShouldRecoverProcessingJobAsync(string jobId)
    {
        if (_redis == null)
        {
            return true;
        }

        try
        {
            var claimValue = await _redis.GetDatabase()
                .StringGetAsync($"{ClaimKeyPrefix}{jobId}")
                .ConfigureAwait(false);
            return claimValue.IsNullOrEmpty;
        }
        // Intentional catch-all: this is a per-job check inside the startup
        // recovery loop; if the Redis claim can't be inspected, treat the job as
        // still owned elsewhere (skip recovery) rather than aborting the sweep.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TileOperationLog.RecoveryClaimInspectionFailed(_logger, jobId, ex);
            return false;
        }
    }

    private Task<RedisLeaseCoordinator?> TryAcquireJobLeaseAsync(string jobId)
    {
        if (_redis == null)
        {
            return Task.FromResult<RedisLeaseCoordinator?>(null);
        }

        return TryAcquireLeaseAsync(new RedisLeaseCoordinator(_redis, $"{ClaimKeyPrefix}{jobId}", ClaimTtl));
    }

    private async Task RenewLeaseAsync(RedisLeaseCoordinator leaseCoordinator, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ClaimTtl.TotalMilliseconds / 3d), cancellationToken).ConfigureAwait(false);
                if (!await leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false))
                {
                    TileOperationLog.LeaseRenewalLost(_logger);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected: the lease was released (or the job finished) and renewal was cancelled.
        }
    }

    private static async Task<RedisLeaseCoordinator?> TryAcquireLeaseAsync(RedisLeaseCoordinator leaseCoordinator)
        => await leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false)
            ? leaseCoordinator
            : null;

    private readonly record struct CachedTileOperationRequest(TileOperationStartRequest Request, string? SchemaName, DateTimeOffset ExpiresAtUtc);

    [LoggerMessage(EventId = 9200, Level = LogLevel.Warning, Message = "Tile job {JobId} failed during {Operation}.")]
    private static partial void LogJobFailed(ILogger logger, string jobId, string operation, Exception exception);
}

internal sealed record PersistedTileOperationRequest
{
    public required TileOperationStartRequest Request { get; init; }
    public string? SchemaName { get; init; }
}

internal sealed record TileOperationStartRequest
{
    public required string Operation { get; init; }
    public string? ServiceId { get; init; }
    public int? LayerId { get; init; }
    public int? MinZoom { get; init; }
    public int? MaxZoom { get; init; }
    public string? TileMatrixSetId { get; init; }
    public double[]? Bbox { get; init; }
    public int? MaxTiles { get; init; }

    /// <summary>
    /// Stable generation identifier for a resumable seed/warm run (issue #2661). Optional so all
    /// existing seed/warm/invalidate/purge/archive/publish callers are unchanged; when absent the
    /// in-process submission path stamps one, and a retry forwards it so the generation resumes
    /// rather than restarting from zero.
    /// </summary>
    public string? GenerationId { get; init; }
}
