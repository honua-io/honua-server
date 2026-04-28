// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Progress;
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
    IConnectionMultiplexer? redis = null) : ITileOperationJobService
{
    private const string MissingRequestFailureMessage = "Tile operation request metadata is no longer available.";
    private readonly IUniversalProgressStore _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
    private readonly IDistributedCache? _requestCache = requestCache;
    private readonly OutputCacheInvalidationService _cacheInvalidationService = cacheInvalidationService ?? throw new ArgumentNullException(nameof(cacheInvalidationService));
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
    private readonly TileOptions _tileOptions = tileOptions?.Value ?? throw new ArgumentNullException(nameof(tileOptions));
    private readonly TileLimits _tileLimits = limitsOptions?.Value?.Tiles ?? throw new ArgumentNullException(nameof(limitsOptions));
    private readonly ILogger<TileOperationJobService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConnectionMultiplexer? _redis = redis;

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
            tokenSource.Cancel();
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

                    var layerCatalog = scope.ServiceProvider.GetRequiredService<ILayerCatalog>();
                    var tileProvider = scope.ServiceProvider.GetRequiredService<ITileProvider>();

                    finalProgress = request.Operation switch
                    {
                        "seed" => await ExecuteSeedOrWarmAsync(started, request, warmMode: false, layerCatalog, tileProvider, linkedCts.Token).ConfigureAwait(false),
                        "warm" => await ExecuteSeedOrWarmAsync(started, request, warmMode: true, layerCatalog, tileProvider, linkedCts.Token).ConfigureAwait(false),
                        "invalidate" => await ExecuteInvalidationAsync(started, request, layerCatalog, linkedCts.Token).ConfigureAwait(false),
                        "purge" => await ExecuteInvalidationAsync(started, request, layerCatalog, linkedCts.Token).ConfigureAwait(false),
                        "archive" => await ExecuteArchiveAsync(started, request, layerCatalog, tileProvider, scope.ServiceProvider, linkedCts.Token).ConfigureAwait(false),
                        "publish" => await ExecutePublishAsync(started, request, layerCatalog, tileProvider, scope.ServiceProvider, linkedCts.Token).ConfigureAwait(false),
                        _ => started with
                        {
                            Status = OperationStatus.Failed,
                            CompletedAt = DateTimeOffset.UtcNow,
                            ErrorMessage = $"Unsupported tile operation '{request.Operation}'.",
                            CurrentPhase = "Failed"
                        }
                    };
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
            catch (Exception ex)
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
            if (renewalCts != null)
            {
                renewalCts.Cancel();
                await renewalTask.ConfigureAwait(false);
                await leaseCoordinator!.ReleaseAsync().ConfigureAwait(false);
                leaseCoordinator.Dispose();
            }
        }
    }

    private async Task<TileOperationProgress> ExecuteInvalidationAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        ILayerCatalog layerCatalog,
        CancellationToken cancellationToken)
    {
        if (request.LayerId.HasValue)
        {
            await _cacheInvalidationService.InvalidateLayerAsync(request.ServiceId, request.LayerId.Value, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            var service = await layerCatalog.GetServiceAsync(request.ServiceId, cancellationToken).ConfigureAwait(false);
            var layerIds = service?.Layers.Select(static layer => layer.Id) ?? [];
            await _cacheInvalidationService.InvalidateServiceCatalogAsync(request.ServiceId, layerIds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _cacheInvalidationService.InvalidateOgcMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        return progress with
        {
            Status = OperationStatus.Completed,
            TotalTiles = 1,
            ProcessedTiles = 1,
            SuccessfulTiles = 1,
            FailedTiles = 0,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = $"{request.Operation} completed"
        };
    }

    private async Task<TileOperationProgress> ExecuteSeedOrWarmAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        bool warmMode,
        ILayerCatalog layerCatalog,
        ITileProvider tileProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        var layerIds = await ResolveLayerIdsAsync(request, layerCatalog, cancellationToken).ConfigureAwait(false);
        if (layerIds.Count == 0)
        {
            throw new InvalidOperationException("Unable to resolve a target layer for tile operation.");
        }

        var tileCoordinates = BuildTileCoordinates(request);
        if (tileCoordinates.Count == 0)
        {
            throw new InvalidOperationException("Tile operation produced no target tiles.");
        }

        var total = (long)layerIds.Count * tileCoordinates.Count;
        var processed = 0L;
        var successful = 0L;
        var failed = 0L;
        var warningList = new List<string>();
        var phase = warmMode ? "Warming tiles" : "Seeding tiles";

        var current = progress with
        {
            TotalTiles = total,
            CurrentPhase = phase
        };
        await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);

        foreach (var layerId in layerIds)
        {
            foreach (var coordinate in tileCoordinates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await tileProvider.GetMvtTileAsync(
                        layerId,
                        coordinate.X,
                        coordinate.Y,
                        coordinate.Z,
                        query: null,
                        _tileOptions,
                        _tileLimits,
                        cancellationToken).ConfigureAwait(false);
                    successful++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    if (warningList.Count < 20)
                    {
                        warningList.Add($"Layer {layerId} tile {coordinate.Z}/{coordinate.X}/{coordinate.Y}: {ex.Message}");
                    }
                }

                processed++;
                if (processed % 25 == 0 || processed == total)
                {
                    current = current with
                    {
                        ProcessedTiles = processed,
                        SuccessfulTiles = successful,
                        FailedTiles = failed,
                        Warnings = warningList.ToArray(),
                        CurrentPhase = $"{phase} ({processed}/{total})"
                    };
                    await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (processed > 0)
        {
            TileOperationMetrics.TileThroughput.Add(
                processed,
                new TagList
                {
                    { "operation", request.Operation }
                });
        }

        var completedStatus = failed == 0 ? OperationStatus.Completed : OperationStatus.Failed;
        return current with
        {
            Status = completedStatus,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = failed == 0 ? null : $"{failed} tiles failed to process.",
            CurrentPhase = failed == 0 ? $"{request.Operation} completed" : $"{request.Operation} completed with failures"
        };
    }

    private async Task<TileOperationProgress> ExecuteArchiveAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        ILayerCatalog layerCatalog,
        ITileProvider tileProvider,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        if (!request.LayerId.HasValue)
        {
            throw new InvalidOperationException("Archive operations require a layerId.");
        }

        var layerId = request.LayerId.Value;

        var build = await BuildPMTilesArchiveAsync("archive", progress, request, layerId, tileProvider, cancellationToken).ConfigureAwait(false);
        if (!build.HasArchive)
        {
            return build.Progress;
        }

        await using var archiveStream = build.ArchiveStream!;

        var current = build.Progress with { CurrentPhase = "Uploading archive" };
        await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);

        var cloudStorage = serviceProvider.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Cloud storage is not configured. Archive operations require cloud storage.",
                CurrentPhase = "Failed"
            };
        }

        var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
        {
            Content = archiveStream,
            FileName = $"{current.JobId}.pmtiles",
            ContentType = "application/vnd.pmtiles",
            SizeBytes = build.ArchiveSize,
            TimeToLive = TimeSpan.FromHours(24),
            Folder = "pmtiles",
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add("jobId", current.JobId)
                .Add("operation", "archive")
        }, cancellationToken).ConfigureAwait(false);

        if (!uploadResult.Success)
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"Archive upload failed: {uploadResult.ErrorMessage ?? "unknown error"}.",
                CurrentPhase = "Failed"
            };
        }

        var archiveFileId = uploadResult.File?.FileId;
        if (string.IsNullOrWhiteSpace(archiveFileId))
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Archive upload succeeded but returned no file ID.",
                CurrentPhase = "Failed"
            };
        }

        var downloadUrl = await cloudStorage.GetPresignedUrlAsync(
            archiveFileId,
            TimeSpan.FromHours(24),
            cancellationToken).ConfigureAwait(false);

        TileOperationMetrics.ArchivesGenerated.Add(1);
        TileOperationMetrics.ArchiveSizeBytes.Record(build.ArchiveSize);

        var completedStatus = build.Failed > 0 ? OperationStatus.Failed : OperationStatus.Completed;
        return current with
        {
            ArchiveSizeBytes = build.ArchiveSize,
            ArchiveFileId = archiveFileId,
            DownloadUrl = downloadUrl,
            Status = completedStatus,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = build.Failed > 0 ? $"{build.Failed} tiles failed during generation." : null,
            CurrentPhase = build.Failed > 0 ? "Archive completed with failures" : "Archive generation completed"
        };
    }

    private async Task<TileOperationProgress> ExecutePublishAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        ILayerCatalog layerCatalog,
        ITileProvider tileProvider,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        if (!request.LayerId.HasValue)
        {
            throw new InvalidOperationException("Publish operations require a layerId.");
        }

        var layerId = request.LayerId.Value;

        var cloudOptions = serviceProvider.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var publishOptions = cloudOptions.PMTilesPublish ?? new PMTilesPublishOptions();

        // Pre-flight: validate URL strategy configuration before generating or uploading
        // a permanent artifact, so a misconfigured PublicUrl never produces an orphan.
        if (publishOptions.UrlStrategy == PMTilesUrlStrategy.PublicUrl &&
            string.IsNullOrWhiteSpace(publishOptions.PublicBucketBaseUrl))
        {
            return progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "PMTilesPublish:PublicBucketBaseUrl must be configured when UrlStrategy is PublicUrl.",
                CurrentPhase = "Failed"
            };
        }

        var build = await BuildPMTilesArchiveAsync("publish", progress, request, layerId, tileProvider, cancellationToken).ConfigureAwait(false);
        if (!build.HasArchive)
        {
            return build.Progress;
        }

        await using var archiveStream = build.ArchiveStream!;

        var cloudStorage = serviceProvider.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            return build.Progress with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Cloud storage is not configured. Publish operations require cloud storage.",
                CurrentPhase = "Failed"
            };
        }

        var providerKeyPrefix = cloudOptions.Provider switch
        {
            CloudStorageProvider.AwsS3 => cloudOptions.AwsS3?.KeyPrefix,
            CloudStorageProvider.AzureBlob => cloudOptions.AzureBlob?.BlobPrefix,
            _ => null
        };

        var objectKey = BuildPublishObjectKey(
            providerKeyPrefix,
            publishOptions.KeyPrefix,
            request.ServiceId,
            layerId,
            request.TileMatrixSetId);

        var current = build.Progress with { CurrentPhase = "Uploading durable PMTiles artifact" };
        await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
        TileOperationLog.PublishUploadStart(_logger, current.JobId, objectKey);

        // Pre-check whether the deterministic key already has a prior published
        // artifact. If it does, our upload is an overwrite of a previously good
        // publish, so a later access-URL failure must not roll back this key:
        // doing so would erase the only PMTiles bytes clients currently see.
        var keyAlreadyExisted = await cloudStorage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false);

        var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
        {
            Content = archiveStream,
            FileName = $"{Path.GetFileName(objectKey)}",
            ContentType = "application/vnd.pmtiles",
            SizeBytes = build.ArchiveSize,
            TimeToLive = null,
            ObjectKeyOverride = objectKey,
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add("jobId", current.JobId)
                .Add("operation", "publish")
                .Add("layerId", layerId.ToString(CultureInfo.InvariantCulture))
        }, cancellationToken).ConfigureAwait(false);

        if (!uploadResult.Success || uploadResult.File is null)
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"Publish upload failed: {uploadResult.ErrorMessage ?? "unknown error"}.",
                CurrentPhase = "Failed"
            };
        }

        var bucket = ResolvePublishBucket(cloudOptions);
        var artifactId = uploadResult.File.FileId;

        string accessUrl;
        DateTimeOffset? accessUrlExpiresAt;
        try
        {
            (accessUrl, accessUrlExpiresAt) = await ResolvePublishAccessUrlAsync(
                cloudStorage,
                publishOptions,
                artifactId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TileOperationLog.PublishAccessUrlFailed(_logger, current.JobId, artifactId, publishOptions.UrlStrategy, ex);
            if (!keyAlreadyExisted)
            {
                // The durable artifact is brand new at this key, so deleting it
                // only removes the failed upload — there was no prior good copy
                // to preserve. This keeps a misconfigured PublicUrl / SignedUrl
                // strategy from leaving an unreferenced orphan.
                await TryDeletePublishArtifactAsync(cloudStorage, artifactId, current.JobId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The key already had a previously good publish that this upload
                // overwrote. The new bytes are still valid PMTiles content; only
                // access-URL generation failed. Leave the artifact in place so
                // existing clients continue to see PMTiles at this key, and let
                // the operator re-run publish once the URL strategy is fixed.
                TileOperationLog.PublishOverwriteRetained(_logger, current.JobId, artifactId, publishOptions.UrlStrategy);
            }

            // Provider exceptions can carry SDK internals, account identifiers, or
            // signed-URL fragments; surface a stable client-safe message and rely
            // on structured logging for the diagnostic detail.
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Publish access URL generation failed.",
                CurrentPhase = "Failed"
            };
        }

        var descriptor = new PMTilesArtifactDescriptor
        {
            ArtifactId = artifactId,
            StorageProvider = uploadResult.File.Provider,
            Bucket = bucket,
            ObjectKey = uploadResult.File.StoragePath,
            ContentType = uploadResult.File.ContentType,
            SizeBytes = uploadResult.File.SizeBytes,
            UrlStrategy = publishOptions.UrlStrategy,
            AccessUrl = accessUrl,
            AccessUrlExpiresAt = accessUrlExpiresAt,
            PublishedAt = uploadResult.File.UploadedAt,
            MinZoom = build.MinZoom,
            MaxZoom = build.MaxZoom,
            Bounds = [build.MinLon, build.MinLat, build.MaxLon, build.MaxLat],
            LayerId = layerId,
            ServiceId = request.ServiceId,
            TileMatrixSetId = request.TileMatrixSetId
        };

        TileOperationMetrics.ArchivesGenerated.Add(1);
        TileOperationMetrics.ArchiveSizeBytes.Record(build.ArchiveSize);
        TileOperationLog.PublishUploadComplete(_logger, current.JobId, descriptor.ObjectKey, build.ArchiveSize, descriptor.UrlStrategy);

        var completedStatus = build.Failed > 0 ? OperationStatus.Failed : OperationStatus.Completed;
        return current with
        {
            ArchiveSizeBytes = build.ArchiveSize,
            PublishedArtifact = descriptor,
            Status = completedStatus,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = build.Failed > 0 ? $"{build.Failed} tiles failed during generation." : null,
            CurrentPhase = build.Failed > 0 ? "Publish completed with failures" : "Publish completed"
        };
    }

    /// <summary>
    /// Generates tiles for the requested scope, writes them to a PMTiles archive
    /// stream, and updates progress along the way. Shared between the temporary
    /// admin <c>archive</c> path and the durable <c>publish</c> path so that
    /// progress, telemetry, geodesy, and failure semantics stay aligned.
    /// </summary>
    private async Task<PMTilesBuildResult> BuildPMTilesArchiveAsync(
        string operationName,
        TileOperationProgress progress,
        TileOperationStartRequest request,
        int layerId,
        ITileProvider tileProvider,
        CancellationToken cancellationToken)
    {
        var tileCoordinates = BuildTileCoordinates(request);
        if (tileCoordinates.Count == 0)
        {
            throw new InvalidOperationException($"{operationName} operation produced no target tiles.");
        }

        var total = (long)tileCoordinates.Count;
        long processed = 0;
        long successful = 0;
        long failed = 0;
        var warningList = new List<string>();

        var current = progress with
        {
            TotalTiles = total,
            CurrentPhase = $"Generating tiles for {operationName}"
        };
        await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);

        var writer = new PMTilesWriter(tileCompression: PMTilesCompression.None);
        foreach (var coordinate in tileCoordinates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var tileData = await tileProvider.GetMvtTileAsync(
                    layerId,
                    coordinate.X,
                    coordinate.Y,
                    coordinate.Z,
                    query: null,
                    _tileOptions,
                    _tileLimits,
                    cancellationToken).ConfigureAwait(false);

                if (tileData is { Length: > 0 })
                {
                    writer.AddTile(coordinate.Z, coordinate.X, coordinate.Y, tileData);
                    successful++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                if (warningList.Count < 20)
                {
                    warningList.Add($"Layer {layerId} tile {coordinate.Z}/{coordinate.X}/{coordinate.Y}: {ex.Message}");
                }
            }

            processed++;
            if (processed % 25 == 0 || processed == total)
            {
                current = current with
                {
                    ProcessedTiles = processed,
                    SuccessfulTiles = successful,
                    FailedTiles = failed,
                    Warnings = warningList.ToArray(),
                    CurrentPhase = $"Generating tiles ({processed}/{total})"
                };
                await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
            }
        }

        if (processed > 0)
        {
            TileOperationMetrics.TileThroughput.Add(processed, new TagList { { "operation", operationName } });
        }

        if (writer.TileCount == 0)
        {
            return PMTilesBuildResult.Empty(current with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"No tiles were generated for the {operationName}.",
                CurrentPhase = "Failed"
            });
        }

        current = current with { CurrentPhase = "Writing PMTiles archive" };
        await _progressStore.SetProgressAsync(current.JobId, current, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);

        var bbox = request.Bbox is { Length: 4 }
            ? request.Bbox
            : [-180d, -SpatialConstants.WebMercatorMaxLatitude, 180d, SpatialConstants.WebMercatorMaxLatitude];

        var minZoom = Math.Clamp(request.MinZoom ?? _tileLimits.MinTileZoom, _tileLimits.MinTileZoom, _tileLimits.MaxTileZoom);
        var maxZoom = Math.Clamp(request.MaxZoom ?? minZoom, minZoom, _tileLimits.MaxTileZoom);

        var minLon = Math.Min(bbox[0], bbox[2]);
        var minLat = Math.Min(bbox[1], bbox[3]);
        var maxLon = Math.Max(bbox[0], bbox[2]);
        var maxLat = Math.Max(bbox[1], bbox[3]);

        var metadata = new PMTilesArchiveMetadata
        {
            MinLon = minLon,
            MinLat = minLat,
            MaxLon = maxLon,
            MaxLat = maxLat,
            MinZoom = minZoom,
            MaxZoom = maxZoom
        };

        var archiveStream = new MemoryStream();
        try
        {
            var archiveSize = await writer.WriteAsync(archiveStream, metadata, cancellationToken).ConfigureAwait(false);
            archiveStream.Position = 0;

            return new PMTilesBuildResult
            {
                Progress = current with
                {
                    ProcessedTiles = processed,
                    SuccessfulTiles = successful,
                    FailedTiles = failed,
                    Warnings = warningList.ToArray()
                },
                ArchiveStream = archiveStream,
                ArchiveSize = archiveSize,
                Failed = failed,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                MinLon = minLon,
                MinLat = minLat,
                MaxLon = maxLon,
                MaxLat = maxLat
            };
        }
        catch
        {
            await archiveStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryDeletePublishArtifactAsync(
        ICloudFileStorage cloudStorage,
        string artifactId,
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Real providers (S3, Azure Blob, LocalFileStorage) report delete failure
            // by returning false rather than throwing, so the bool result is the only
            // signal a soft failure ever leaves an orphan worth flagging.
            var deleted = await cloudStorage.DeleteAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                TileOperationLog.PublishOrphanCleanupReturnedFalse(_logger, jobId, artifactId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TileOperationLog.PublishOrphanCleanupFailed(_logger, jobId, artifactId, ex);
        }
    }

    private static string BuildPublishObjectKey(
        string? providerKeyPrefix,
        string publishKeyPrefix,
        string? serviceId,
        int layerId,
        string? tileMatrixSetId)
    {
        var serviceSegment = string.IsNullOrWhiteSpace(serviceId)
            ? "_"
            : SanitizeKeySegment(serviceId);
        var matrixSegment = string.IsNullOrWhiteSpace(tileMatrixSetId)
            ? "WebMercatorQuad"
            : SanitizeKeySegment(tileMatrixSetId);

        var segments = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(providerKeyPrefix))
        {
            segments.Add(providerKeyPrefix.Trim('/'));
        }

        if (!string.IsNullOrWhiteSpace(publishKeyPrefix))
        {
            segments.Add(publishKeyPrefix.Trim('/'));
        }

        segments.Add(serviceSegment);
        segments.Add(layerId.ToString(CultureInfo.InvariantCulture));
        segments.Add($"{matrixSegment}.pmtiles");

        return string.Join('/', segments.Where(static segment => segment.Length > 0));
    }

    private static string SanitizeKeySegment(string value)
    {
        var trimmed = value.Trim().Trim('/');
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static string ResolvePublishBucket(CloudStorageOptions options)
    {
        return options.Provider switch
        {
            CloudStorageProvider.AwsS3 => options.AwsS3?.BucketName ?? string.Empty,
            CloudStorageProvider.AzureBlob => options.AzureBlob?.ContainerName ?? string.Empty,
            CloudStorageProvider.Local => options.LocalStorage?.BasePath ?? string.Empty,
            _ => string.Empty
        };
    }

    private static async Task<(string AccessUrl, DateTimeOffset? ExpiresAt)> ResolvePublishAccessUrlAsync(
        ICloudFileStorage cloudStorage,
        PMTilesPublishOptions publishOptions,
        string artifactId,
        CancellationToken cancellationToken)
    {
        switch (publishOptions.UrlStrategy)
        {
            case PMTilesUrlStrategy.PublicUrl:
                if (string.IsNullOrWhiteSpace(publishOptions.PublicBucketBaseUrl))
                {
                    throw new InvalidOperationException(
                        "PMTilesPublish:PublicBucketBaseUrl must be configured when UrlStrategy is PublicUrl.");
                }

                var publicBase = publishOptions.PublicBucketBaseUrl.TrimEnd('/');
                return ($"{publicBase}/{artifactId}", null);

            case PMTilesUrlStrategy.RangeProxy:
                return ($"/api/v1/tiles/pmtiles/{artifactId}", null);

            case PMTilesUrlStrategy.SignedUrl:
            default:
                var lifetime = publishOptions.SignedUrlLifetime <= TimeSpan.Zero
                    ? TimeSpan.FromDays(7)
                    : publishOptions.SignedUrlLifetime;
                var signedUrl = await cloudStorage.GetPresignedUrlAsync(artifactId, lifetime, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(signedUrl))
                {
                    throw new InvalidOperationException("Storage provider did not return a presigned URL for the published artifact.");
                }

                return (signedUrl, DateTimeOffset.UtcNow.Add(lifetime));
        }
    }

    private static async Task<IReadOnlyList<int>> ResolveLayerIdsAsync(
        TileOperationStartRequest request,
        ILayerCatalog layerCatalog,
        CancellationToken cancellationToken)
    {
        if (request.LayerId.HasValue)
        {
            return [request.LayerId.Value];
        }

        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return [];
        }

        var service = await layerCatalog.GetServiceAsync(request.ServiceId, cancellationToken).ConfigureAwait(false);
        if (service is null || service.Layers.Length == 0)
        {
            return [];
        }

        return service.Layers.Select(static layer => layer.Id).Distinct().ToArray();
    }

    private List<TileCoordinate> BuildTileCoordinates(TileOperationStartRequest request)
    {
        var maxTiles = Math.Clamp(request.MaxTiles ?? 500, 1, 5_000);
        var minZoom = Math.Clamp(request.MinZoom ?? _tileLimits.MinTileZoom, _tileLimits.MinTileZoom, _tileLimits.MaxTileZoom);
        var maxZoom = Math.Clamp(request.MaxZoom ?? minZoom, minZoom, _tileLimits.MaxTileZoom);

        var bbox = request.Bbox is { Length: 4 }
            ? request.Bbox
            : [-180d, -SpatialConstants.WebMercatorMaxLatitude, 180d, SpatialConstants.WebMercatorMaxLatitude];

        var minLon = Math.Min(bbox[0], bbox[2]);
        var maxLon = Math.Max(bbox[0], bbox[2]);
        var minLat = Math.Min(bbox[1], bbox[3]);
        var maxLat = Math.Max(bbox[1], bbox[3]);

        var result = new List<TileCoordinate>(Math.Min(maxTiles, 1024));
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var n = 1 << z;
            var xMin = LonToTileX(minLon, z, n);
            var xMax = LonToTileX(maxLon, z, n);
            var yMin = LatToTileY(maxLat, z, n);
            var yMax = LatToTileY(minLat, z, n);

            for (var x = xMin; x <= xMax; x++)
            {
                for (var y = yMin; y <= yMax; y++)
                {
                    result.Add(new TileCoordinate(z, x, y));
                    if (result.Count >= maxTiles)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    private static int LonToTileX(double lon, int z, int n)
    {
        var clampedLon = Math.Clamp(lon, -180d, 180d);
        var x = (int)Math.Floor((clampedLon + 180d) / 360d * n);
        return Math.Clamp(x, 0, n - 1);
    }

    private static int LatToTileY(double lat, int z, int n)
    {
        var clampedLat = Math.Clamp(lat, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var latRad = clampedLat * Math.PI / 180d;
        var y = (int)Math.Floor(
            (1d - Math.Log(Math.Tan(latRad) + (1d / Math.Cos(latRad))) / Math.PI) / 2d * n);
        return Math.Clamp(y, 0, n - 1);
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
            catch (Exception ex)
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
        catch (Exception ex)
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
        }
    }

    private static async Task<RedisLeaseCoordinator?> TryAcquireLeaseAsync(RedisLeaseCoordinator leaseCoordinator)
        => await leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false)
            ? leaseCoordinator
            : null;

    private readonly record struct TileCoordinate(int Z, int X, int Y);
    private readonly record struct CachedTileOperationRequest(TileOperationStartRequest Request, string? SchemaName, DateTimeOffset ExpiresAtUtc);

    private sealed record PMTilesBuildResult
    {
        public required TileOperationProgress Progress { get; init; }
        public MemoryStream? ArchiveStream { get; init; }
        public long ArchiveSize { get; init; }
        public long Failed { get; init; }
        public int MinZoom { get; init; }
        public int MaxZoom { get; init; }
        public double MinLon { get; init; }
        public double MinLat { get; init; }
        public double MaxLon { get; init; }
        public double MaxLat { get; init; }

        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(ArchiveStream))]
        public bool HasArchive => ArchiveStream is not null;

        public static PMTilesBuildResult Empty(TileOperationProgress progress) => new() { Progress = progress };
    }

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
}
