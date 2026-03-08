// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

internal interface ITileOperationJobService
{
    Task<string> StartAsync(TileOperationStartRequest request, CancellationToken cancellationToken = default);
    Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);
    Task<string?> RetryAsync(string jobId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> ReadQueuedJobIdsAsync(CancellationToken cancellationToken = default);
    Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default);
}

internal sealed partial class TileOperationJobService(
    IUniversalProgressStore progressStore,
    OutputCacheInvalidationService cacheInvalidationService,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<TileOptions> tileOptions,
    IOptions<LimitsOptions> limitsOptions,
    ILogger<TileOperationJobService> logger) : ITileOperationJobService
{
    private readonly IUniversalProgressStore _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
    private readonly OutputCacheInvalidationService _cacheInvalidationService = cacheInvalidationService ?? throw new ArgumentNullException(nameof(cacheInvalidationService));
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
    private readonly TileOptions _tileOptions = tileOptions?.Value ?? throw new ArgumentNullException(nameof(tileOptions));
    private readonly TileLimits _tileLimits = limitsOptions?.Value?.Tiles ?? throw new ArgumentNullException(nameof(limitsOptions));
    private readonly ILogger<TileOperationJobService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private const int JobRequestRetentionHours = 24;
    private static readonly TimeSpan _jobRequestRetention = TimeSpan.FromHours(JobRequestRetentionHours);

    private readonly ConcurrentDictionary<string, CachedTileOperationRequest> _jobRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTokens = new(StringComparer.Ordinal);
    private readonly Channel<string> _jobQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public async Task<string> StartAsync(TileOperationStartRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PruneExpiredJobRequests();

        var normalized = NormalizeRequest(request);
        var jobId = Guid.NewGuid().ToString("N");
        _jobRequests[jobId] = CreateCachedRequest(normalized);

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
        var operationIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.TileCache, cancellationToken).ConfigureAwait(false);
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
        if (!TryGetActiveJobRequest(jobId, out var originalRequest))
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
        return await StartAsync(originalRequest, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<string> ReadQueuedJobIdsAsync(CancellationToken cancellationToken = default)
    {
        return _jobQueue.Reader.ReadAllAsync(cancellationToken);
    }

    public async Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!TryGetActiveJobRequest(jobId, out var request))
        {
            return;
        }

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
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runningTokens[jobId] = linkedCts;

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
            var layerCatalog = scope.ServiceProvider.GetRequiredService<ILayerCatalog>();
            var tileProvider = scope.ServiceProvider.GetRequiredService<ITileProvider>();

            finalProgress = request.Operation switch
            {
                "seed" => await ExecuteSeedOrWarmAsync(started, request, warmMode: false, layerCatalog, tileProvider, linkedCts.Token).ConfigureAwait(false),
                "warm" => await ExecuteSeedOrWarmAsync(started, request, warmMode: true, layerCatalog, tileProvider, linkedCts.Token).ConfigureAwait(false),
                "invalidate" => await ExecuteInvalidationAsync(started, request, layerCatalog, linkedCts.Token).ConfigureAwait(false),
                "purge" => await ExecuteInvalidationAsync(started, request, layerCatalog, linkedCts.Token).ConfigureAwait(false),
                _ => started with
                {
                    Status = OperationStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Unsupported tile operation '{request.Operation}'.",
                    CurrentPhase = "Failed"
                }
            };
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            finalProgress = (TileOperationProgress)started.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");
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
        }
        else
        {
            RefreshJobRequestRetention(jobId);
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
            : [-180d, -85.05112878d, 180d, 85.05112878d];

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
        var clampedLat = Math.Clamp(lat, -85.05112878d, 85.05112878d);
        var latRad = clampedLat * Math.PI / 180d;
        var y = (int)Math.Floor(
            (1d - Math.Log(Math.Tan(latRad) + (1d / Math.Cos(latRad))) / Math.PI) / 2d * n);
        return Math.Clamp(y, 0, n - 1);
    }

    private static TileOperationStartRequest NormalizeRequest(TileOperationStartRequest request)
    {
        var operation = request.Operation.Trim().ToLowerInvariant();
        if (operation is not ("seed" or "warm" or "invalidate" or "purge"))
        {
            throw new ArgumentException("Operation must be one of: seed, warm, invalidate, purge.", nameof(request));
        }

        return request with
        {
            Operation = operation,
            TileMatrixSetId = string.IsNullOrWhiteSpace(request.TileMatrixSetId)
                ? "WebMercatorQuad"
                : request.TileMatrixSetId.Trim()
        };
    }

    private static CachedTileOperationRequest CreateCachedRequest(TileOperationStartRequest request)
    {
        return new CachedTileOperationRequest(request, DateTimeOffset.UtcNow.Add(_jobRequestRetention));
    }

    private bool TryGetActiveJobRequest(string jobId, out TileOperationStartRequest request)
    {
        if (!_jobRequests.TryGetValue(jobId, out var cachedRequest))
        {
            request = null!;
            return false;
        }

        if (cachedRequest.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _jobRequests.TryRemove(jobId, out _);
            request = null!;
            return false;
        }

        request = cachedRequest.Request;
        return true;
    }

    private void RefreshJobRequestRetention(string jobId)
    {
        if (!TryGetActiveJobRequest(jobId, out var request))
        {
            return;
        }

        _jobRequests[jobId] = CreateCachedRequest(request);
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

    private readonly record struct TileCoordinate(int Z, int X, int Y);
    private readonly record struct CachedTileOperationRequest(TileOperationStartRequest Request, DateTimeOffset ExpiresAtUtc);

    [LoggerMessage(EventId = 9200, Level = LogLevel.Warning, Message = "Tile job {JobId} failed during {Operation}.")]
    private static partial void LogJobFailed(ILogger logger, string jobId, string operation, Exception exception);
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
