// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Export.Writers;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.Export;

internal interface IExportJobService
{
    Task<string> StartAsync(ExportJob job, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> ReadQueuedJobIdsAsync(CancellationToken cancellationToken = default);
    Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default);
}

internal sealed class ExportJobService(
    IUniversalProgressStore progressStore,
    IDistributedCache? requestCache,
    Channel<string> jobQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<ExportJobService> logger,
    IConnectionMultiplexer? redis = null,
    TimeSpan? recoveryPollInterval = null) : IExportJobService
{
    private const string ExportFailureMessage = "Export failed.";
    private const string MissingRequestFailureMessage = "Export request metadata is no longer available.";
    private const string DurableQueueUnavailableMessage = "Large exports require durable background job storage to be configured.";
    private const string RecoveredForRetryPhase = "Recovered for retry";
    private const int JobRetentionHours = 24;
    private const string RequestCacheKeyPrefix = "export:request:";
    private const string ClaimKeyPrefix = "export:job:claim:";
    private static readonly TimeSpan _jobRetention = TimeSpan.FromHours(JobRetentionHours);
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromSeconds(30);

    private readonly IUniversalProgressStore _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
    private readonly IDistributedCache? _requestCache = requestCache;
    private readonly Channel<string> _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ILogger<ExportJobService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConnectionMultiplexer? _redis = redis;
    private readonly ConcurrentDictionary<string, CachedExportJob> _jobRequests = new(StringComparer.Ordinal);
    private readonly TimeSpan _recoveryPollInterval = recoveryPollInterval ?? TimeSpan.FromSeconds(10);

    public async Task<string> StartAsync(ExportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_requestCache == null)
        {
            throw new InvalidOperationException(DurableQueueUnavailableMessage);
        }

        PruneExpiredJobRequests();

        _jobRequests[job.JobId] = CreateCachedRequest(job);
        await PersistJobRequestAsync(job.JobId, job, cancellationToken).ConfigureAwait(false);

        var progress = ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures);
        try
        {
            await _progressStore.SetProgressAsync(job.JobId, progress, _jobRetention, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _jobRequests.TryRemove(job.JobId, out _);

            try
            {
                await RemovePersistedJobRequestAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(
                    cleanupEx,
                    "Failed to roll back persisted export request metadata for job {JobId} after the progress record could not be created.",
                    job.JobId);
            }

            throw;
        }

        if (!_jobQueue.Writer.TryWrite(job.JobId))
        {
            _jobRequests.TryRemove(job.JobId, out _);
            await RemovePersistedJobRequestAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            await _progressStore.DeleteProgressAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Export queue is full. Please retry later.");
        }

        return job.JobId;
    }

    public async IAsyncEnumerable<string> ReadQueuedJobIdsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recoveredJobIds = await RecoverQueuedJobIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var jobId in recoveredJobIds)
        {
            yield return jobId;
        }

        var nextRecoveryAt = DateTimeOffset.UtcNow.Add(_recoveryPollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_jobQueue.Reader.TryRead(out var queuedJobId))
            {
                yield return queuedJobId;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextRecoveryAt)
            {
                var recoveredIds = await RecoverQueuedJobIdsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var jobId in recoveredIds)
                {
                    yield return jobId;
                }

                nextRecoveryAt = now.Add(_recoveryPollInterval);
                continue;
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waitForQueue = _jobQueue.Reader.WaitToReadAsync(waitCts.Token).AsTask();
            var waitForRecovery = Task.Delay(nextRecoveryAt - now, waitCts.Token);
            var completed = await Task.WhenAny(waitForQueue, waitForRecovery).ConfigureAwait(false);
            if (completed == waitForQueue)
            {
                if (!await waitForQueue.ConfigureAwait(false))
                {
                    waitCts.Cancel();
                    yield break;
                }

                waitCts.Cancel();
                continue;
            }

            waitCts.Cancel();
            try
            {
                await waitForQueue.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
            {
                // Ignore expected cancellation for the alternate waiter.
            }
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
        try
        {
            var cachedJob = await TryGetActiveJobAsync(jobId, processingToken).ConfigureAwait(false);
            if (cachedJob == null)
            {
                var missingRequestProgress = await _progressStore.GetProgressAsync<ExportProgress>(jobId, cancellationToken).ConfigureAwait(false);
                await MarkMissingRequestAsync(jobId, missingRequestProgress, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var job = cachedJob.Value.Job;
            var existing = await _progressStore.GetProgressAsync<ExportProgress>(job.JobId, cancellationToken).ConfigureAwait(false);
            if (existing?.Status == OperationStatus.Cancelled)
            {
                _jobRequests.TryRemove(job.JobId, out _);
                await RemovePersistedJobRequestAsync(job.JobId, processingToken).ConfigureAwait(false);
                return;
            }

            var progress = existing is ExportProgress queued
                ? queued with { Status = OperationStatus.Processing, CurrentPhase = "Exporting features" }
                : ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures) with
                {
                    Status = OperationStatus.Processing,
                    CurrentPhase = "Exporting features"
                };
            await _progressStore.SetProgressAsync(job.JobId, progress, _jobRetention, processingToken).ConfigureAwait(false);

            var scratchDir = Path.Combine(Path.GetTempPath(), "honua-export", job.JobId);
            Directory.CreateDirectory(scratchDir);
            var shouldRequeue = false;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var streamingStore = scope.ServiceProvider.GetRequiredService<IStreamingFeatureStore>();
                var crsRegistry = scope.ServiceProvider.GetRequiredService<ICrsRegistry>();
                var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();

                var features = streamingStore.StreamFeaturesAsync(job.LayerId, job.Query, processingToken);
                var outputFile = await WriteToFileAsync(job, features, scratchDir, crsRegistry, _logger, processingToken).ConfigureAwait(false);
                var fileInfo = new FileInfo(outputFile);

                await using var fileStream = File.OpenRead(outputFile);
                var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
                {
                    Content = fileStream,
                    FileName = Path.GetFileName(outputFile),
                    ContentType = GetContentType(job.Format),
                    Folder = "exports",
                    TimeToLive = _jobRetention
                }, processingToken).ConfigureAwait(false);

                var downloadUrl = uploadResult.File is not null
                    ? await cloudStorage.GetPresignedUrlAsync(
                        uploadResult.File.FileId, _jobRetention, processingToken).ConfigureAwait(false)
                    : null;

                var completed = progress with
                {
                    Status = OperationStatus.Completed,
                    ProcessedFeatures = job.TotalFeatures,
                    OutputSizeBytes = fileInfo.Length,
                    DownloadUrl = downloadUrl,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Export completed"
                };
                await _progressStore.SetProgressAsync(job.JobId, completed, _jobRetention, processingToken).ConfigureAwait(false);

                _jobRequests.TryRemove(job.JobId, out _);
                await RemovePersistedJobRequestAsync(job.JobId, processingToken).ConfigureAwait(false);
                ExportLog.AsyncExportCompleted(_logger, job.JobId, job.TotalFeatures, fileInfo.Length);
            }
            catch (OperationCanceledException) when (leaseCoordinator?.LeaseLostToken.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                var requeued = progress with
                {
                    Status = OperationStatus.Queued,
                    ErrorMessage = null,
                    CompletedAt = null,
                    CurrentPhase = "Lease lost; awaiting retry"
                };
                await _progressStore.SetProgressAsync(job.JobId, requeued, _jobRetention, CancellationToken.None).ConfigureAwait(false);
                _jobRequests[job.JobId] = CreateCachedRequest(job);
                await PersistJobRequestAsync(job.JobId, job, CancellationToken.None).ConfigureAwait(false);
                shouldRequeue = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ExportLog.AsyncExportFailed(_logger, job.JobId, ex);

                try
                {
                    var failed = progress with
                    {
                        Status = OperationStatus.Failed,
                        ErrorMessage = ExportFailureMessage,
                        CompletedAt = DateTimeOffset.UtcNow,
                        CurrentPhase = "Export failed"
                    };
                    await _progressStore.SetProgressAsync(job.JobId, failed, _jobRetention, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception progressEx)
                {
                    _logger.LogWarning(progressEx,
                        "Failed to persist error status for export job {JobId} after a processing failure.",
                        job.JobId);
                }

                _jobRequests.TryRemove(job.JobId, out _);
                await RemovePersistedJobRequestAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Directory.Delete(scratchDir, recursive: true);
                }
                catch
                {
                }
            }

            if (shouldRequeue)
            {
                await _jobQueue.Writer.WriteAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
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

    private static async Task<string> WriteToFileAsync(
        ExportJob job,
        IAsyncEnumerable<Feature> features,
        string scratchDir,
        ICrsRegistry crsRegistry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? srsName = null;
        string? srsWkt = null;
        var crs = await crsRegistry.ResolveBySridAsync(job.OutputSrid, cancellationToken).ConfigureAwait(false);
        if (crs.HasValue)
        {
            srsName = $"EPSG:{job.OutputSrid}";
            srsWkt = crs.Value.Wkt;
        }

        var baseName = ExportEndpoints.SanitizeExportFilename(job.ServiceName, job.LayerName);

        switch (job.Format.ToLowerInvariant())
        {
            case "csv":
                {
                    var csvPath = Path.Combine(scratchDir, $"{baseName}.csv");
                    await using var stream = File.Create(csvPath);
                    await CsvExportWriter.WriteAsync(stream, features, job.Fields, cancellationToken).ConfigureAwait(false);
                    return csvPath;
                }
            case "shapefile":
                {
                    var zipPath = Path.Combine(scratchDir, $"{baseName}.zip");
                    await using var stream = File.Create(zipPath);
                    await ShapefileExportWriter.WriteAsync(
                        stream, features, job.Fields, job.GeometryType, srsWkt, logger, cancellationToken).ConfigureAwait(false);
                    return zipPath;
                }
            case "gpkg":
                {
                    var gpkgPath = Path.Combine(scratchDir, $"{baseName}.gpkg");
                    await GeoPackageExportWriter.WriteAsync(
                        gpkgPath, features, job.Fields, job.GeometryType, job.OutputSrid, srsName, srsWkt, cancellationToken).ConfigureAwait(false);
                    return gpkgPath;
                }
            default:
                throw new InvalidOperationException($"Unsupported export format: {job.Format}");
        }
    }

    private async Task<CachedExportJob?> TryGetActiveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobRequests.TryGetValue(jobId, out var cachedJob))
        {
            if (cachedJob.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cachedJob;
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

        var persistedRequest = JsonSerializer.Deserialize(json, ExportJsonContext.Default.PersistedExportJobRequest);
        if (persistedRequest?.Job == null)
        {
            await RemovePersistedJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var restoredJob = CreateCachedRequest(persistedRequest.Job);
        _jobRequests[jobId] = restoredJob;
        return restoredJob;
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

    private async Task MarkMissingRequestAsync(
        string jobId,
        ExportProgress? progress,
        CancellationToken cancellationToken)
    {
        if (progress is not null && progress.Status is OperationStatus.Queued or OperationStatus.Processing)
        {
            try
            {
                var failed = progress with
                {
                    Status = OperationStatus.Failed,
                    ErrorMessage = MissingRequestFailureMessage,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Export failed"
                };
                await _progressStore.SetProgressAsync(jobId, failed, _jobRetention, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist missing-request status for export job {JobId}.",
                    jobId);
            }
        }

        _jobRequests.TryRemove(jobId, out _);
        await RemovePersistedJobRequestAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistJobRequestAsync(string jobId, ExportJob job, CancellationToken cancellationToken)
    {
        if (_requestCache == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            new PersistedExportJobRequest { Job = job },
            ExportJsonContext.Default.PersistedExportJobRequest);
        await _requestCache.SetStringAsync(
                GetRequestCacheKey(jobId),
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _jobRetention },
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
        var activeOperationIds = await _progressStore.GetActiveOperationIdsAsync(OperationType.Export, cancellationToken).ConfigureAwait(false);
        var recovered = new List<string>(activeOperationIds.Count);

        foreach (var jobId in activeOperationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = await _progressStore.GetProgressAsync<ExportProgress>(jobId, cancellationToken).ConfigureAwait(false);
            if (progress == null || progress.Status is not (OperationStatus.Queued or OperationStatus.Processing))
            {
                continue;
            }

            var request = await TryGetActiveJobAsync(jobId, cancellationToken).ConfigureAwait(false);
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
                    CurrentPhase = RecoveredForRetryPhase
                };
                await _progressStore.SetProgressAsync(jobId, progress, _jobRetention, cancellationToken).ConfigureAwait(false);
            }

            recovered.Add(jobId);
        }

        return recovered;
    }

    private static string GetRequestCacheKey(string jobId) => $"{RequestCacheKeyPrefix}{jobId}";

    private static string GetContentType(string format) => format.ToLowerInvariant() switch
    {
        "csv" => "text/csv",
        "shapefile" => "application/zip",
        "gpkg" => "application/geopackage+sqlite3",
        _ => "application/octet-stream"
    };

    private static CachedExportJob CreateCachedRequest(ExportJob job)
        => new(job, DateTimeOffset.UtcNow.Add(_jobRetention));

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
            _logger.LogWarning(
                ex,
                "Skipping export recovery for job {JobId} because the Redis claim key could not be inspected.",
                jobId);
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
                    _logger.LogWarning("Export job lease renewal was lost while processing a background export.");
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

    internal sealed record PersistedExportJobRequest
    {
        public required ExportJob Job { get; init; }
    }

    private readonly record struct CachedExportJob(ExportJob Job, DateTimeOffset ExpiresAtUtc);
}
