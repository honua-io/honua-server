// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Server.Features.Import;

/// <summary>
/// Background worker that processes queued GeoServer import jobs.
/// </summary>
internal sealed partial class GeoServerImportBackgroundService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeoServerImportJobManager _jobManager;
    private readonly ILogger<GeoServerImportBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaderCheckInterval = TimeSpan.FromSeconds(10);

    public GeoServerImportBackgroundService(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        IServiceScopeFactory scopeFactory,
        GeoServerImportJobManager jobManager,
        ILogger<GeoServerImportBackgroundService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.ServiceStarting(_logger, _jobManager.LeaderElection.InstanceId);
        var recoveredInFlightJobs = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var isLeader = await _jobManager.LeaderElection.TryAcquireLeadershipAsync(stoppingToken).ConfigureAwait(false);
                if (!isLeader)
                {
                    recoveredInFlightJobs = false;
                    Log.NotLeader(_logger, _jobManager.LeaderElection.InstanceId);
                    await Task.Delay(_leaderCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var heartbeatMaintained = await _jobManager.LeaderElection.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
                if (!heartbeatMaintained)
                {
                    recoveredInFlightJobs = false;
                    Log.NotLeader(_logger, _jobManager.LeaderElection.InstanceId);
                    await Task.Delay(_leaderCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!recoveredInFlightJobs)
                {
                    await _jobManager.JobQueue.RecoverInFlightAsync(stoppingToken).ConfigureAwait(false);
                    recoveredInFlightJobs = true;
                }

                var jobId = await _jobManager.JobQueue.DequeueAsync(_pollInterval, stoppingToken).ConfigureAwait(false);
                if (jobId != null)
                {
                    await ProcessJobAsync(jobId, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ProcessingError(_logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        await _jobManager.LeaderElection.ReleaseLeadershipAsync(CancellationToken.None).ConfigureAwait(false);
        Log.ServiceStopped(_logger, _jobManager.LeaderElection.InstanceId);
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.JobStarted(_logger, jobId);

        GeoServerImportRequest? request = null;
        GeoServerImportProgress? progress = null;
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationTokenSource? monitorCancellation = null;
        Task? monitorTask = null;

        using var progressGate = new SemaphoreSlim(1, 1);
        var finalized = 0;

        async Task SetProgressAsync(GeoServerImportProgress update, CancellationToken token)
        {
            if (Volatile.Read(ref finalized) != 0)
            {
                return;
            }

            await progressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref finalized) != 0)
                {
                    return;
                }

                progress = update;
                await _jobManager.ProgressStore.SetProgressAsync(jobId, update, TimeSpan.FromHours(24), token).ConfigureAwait(false);
            }
            finally
            {
                progressGate.Release();
            }
        }

        async Task SetFinalProgressAsync(GeoServerImportProgress update, CancellationToken token)
        {
            Volatile.Write(ref finalized, 1);
            await progressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                progress = update;
                await _jobManager.ProgressStore.SetProgressAsync(jobId, update, TimeSpan.FromHours(24), token).ConfigureAwait(false);
            }
            finally
            {
                progressGate.Release();
            }
        }

        async Task ReportProgressAsync(GeoServerImportProgress update)
        {
            if (jobCancellation.IsCancellationRequested || Volatile.Read(ref finalized) != 0)
            {
                return;
            }

            try
            {
                await SetProgressAsync(update, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.ProgressUpdateFailed(_logger, jobId, ex);
            }
        }

        async Task MonitorCancellationAsync(string id, CancellationTokenSource jobCts, CancellationToken token)
        {
            while (!token.IsCancellationRequested && !jobCts.IsCancellationRequested)
            {
                try
                {
                    var current = await _jobManager.ProgressStore.GetProgressAsync(id, token).ConfigureAwait(false);
                    if (current?.Status == GeoServerImportStatus.Cancelled)
                    {
                        jobCts.Cancel();
                        return;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.CancellationMonitorPollFailed(_logger, id, ex);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        try
        {
            progress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (progress?.Status == GeoServerImportStatus.Cancelled)
            {
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            request = await _jobManager.RequestStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (request == null)
            {
                Log.JobRequestNotFound(_logger, jobId);

                if (progress == null ||
                    progress.Status is GeoServerImportStatus.Completed or GeoServerImportStatus.Failed or GeoServerImportStatus.Cancelled)
                {
                    return;
                }

                var failedProgress = progress with
                {
                    Status = GeoServerImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Import request not found.",
                    CurrentPhase = "Import request missing"
                };

                await SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                request = request with { JobId = jobId };
            }

            var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(_hostEnvironment, _configuration);
            var urlValidation = await GeoServerServiceUrlValidation.ValidateAsync(
                request.GeoServerRestUrl,
                allowUnsafeLocalUrls,
                stoppingToken).ConfigureAwait(false);
            if (!urlValidation.IsValid)
            {
                var failedProgress = (progress ?? GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl)) with
                {
                    Status = GeoServerImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = urlValidation.ErrorMessage,
                    CurrentPhase = "Import blocked by GeoServer URL validation"
                };

                await SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                Log.JobRejectedUnsafeServiceUrl(_logger, jobId, request.GeoServerRestUrl);
                return;
            }

            monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            monitorTask = MonitorCancellationAsync(jobId, jobCancellation, monitorCancellation.Token);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var importService = scope.ServiceProvider.GetRequiredService<IGeoServerImportService>();
            var progressReporter = new Progress<GeoServerImportProgress>(p => _ = ReportProgressAsync(p));

            var result = await importService.ImportConfigurationAsync(request, progressReporter, jobCancellation.Token).ConfigureAwait(false);

            stopwatch.Stop();

            var currentProgress = progress ?? GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);
            var finalProgress = currentProgress with
            {
                Status = result.Success ? GeoServerImportStatus.Completed : GeoServerImportStatus.Failed,
                ResourcesProcessed = result.WorkspacesImported + result.DataStoresImported + result.LayersImported + result.StylesImported,
                FailedResources = result.FailedResources,
                SourceGeoServerVersion = result.SourceGeoServerVersion ?? currentProgress.SourceGeoServerVersion,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = result.ErrorMessage,
                Warnings = result.Warnings,
                CurrentPhase = result.Success
                    ? (result.WasDryRun ? "Dry run completed" : "Import completed successfully")
                    : "Import failed"
            };

            await SetFinalProgressAsync(finalProgress, stoppingToken).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);

            if (result.Success)
            {
                Log.JobCompleted(_logger, jobId, finalProgress.ResourcesProcessed, stopwatch.Elapsed.TotalSeconds);
            }
            else
            {
                Log.JobFailed(_logger, jobId, result.ErrorMessage ?? "Unknown error", stopwatch.Elapsed.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            stopwatch.Stop();

            var currentProgress = progress ?? GeoServerImportProgress.CreateInitial(
                jobId,
                request?.GeoServerRestUrl ?? string.Empty,
                request?.TargetHonuaUrl ?? string.Empty);
            var cancelledProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import cancelled"
            };

            await SetFinalProgressAsync(cancelledProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var currentProgress = progress ?? GeoServerImportProgress.CreateInitial(
                jobId,
                request?.GeoServerRestUrl ?? string.Empty,
                request?.TargetHonuaUrl ?? string.Empty);
            var failedProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Import failed.",
                CurrentPhase = "Import failed with exception"
            };

            await SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
            Log.JobException(_logger, jobId, ex);
        }
        finally
        {
            try
            {
                await _jobManager.JobQueue.CompleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.JobCompletionAcknowledgeFailed(_logger, jobId, ex);
            }

            if (monitorCancellation != null)
            {
                monitorCancellation.Cancel();
                if (monitorTask != null)
                {
                    try
                    {
                        await monitorTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
                    {
                    }
                }

                monitorCancellation.Dispose();
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7960, LogLevel.Information, "GeoServer import background service starting (instance: {InstanceId})")]
        public static partial void ServiceStarting(ILogger logger, string instanceId);

        [LoggerMessage(7961, LogLevel.Information, "GeoServer import background service stopped (instance: {InstanceId})")]
        public static partial void ServiceStopped(ILogger logger, string instanceId);

        [LoggerMessage(7962, LogLevel.Debug, "Instance {InstanceId} is not the GeoServer import leader, waiting...")]
        public static partial void NotLeader(ILogger logger, string instanceId);

        [LoggerMessage(7963, LogLevel.Information, "Processing GeoServer import job {JobId}")]
        public static partial void JobStarted(ILogger logger, string jobId);

        [LoggerMessage(7964, LogLevel.Information, "GeoServer import job {JobId} completed: {ResourceCount} resources in {DurationSeconds:F1}s")]
        public static partial void JobCompleted(ILogger logger, string jobId, int resourceCount, double durationSeconds);

        [LoggerMessage(7965, LogLevel.Warning, "GeoServer import job {JobId} failed: {ErrorMessage} (duration: {DurationSeconds:F1}s)")]
        public static partial void JobFailed(ILogger logger, string jobId, string errorMessage, double durationSeconds);

        [LoggerMessage(7966, LogLevel.Warning, "GeoServer import job {JobId} cancelled (duration: {DurationSeconds:F1}s)")]
        public static partial void JobCancelled(ILogger logger, string jobId, double durationSeconds);

        [LoggerMessage(7967, LogLevel.Error, "GeoServer import job {JobId} threw exception")]
        public static partial void JobException(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7968, LogLevel.Warning, "GeoServer import job {JobId} request not found in store")]
        public static partial void JobRequestNotFound(ILogger logger, string jobId);

        [LoggerMessage(7969, LogLevel.Warning, "Failed to update progress for GeoServer import job {JobId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7970, LogLevel.Error, "Error in GeoServer import background processing loop")]
        public static partial void ProcessingError(ILogger logger, Exception exception);

        [LoggerMessage(7971, LogLevel.Warning, "Failed to poll cancellation state for GeoServer import job {JobId}")]
        public static partial void CancellationMonitorPollFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7972, LogLevel.Warning, "Failed to acknowledge completion for GeoServer import job {JobId}")]
        public static partial void JobCompletionAcknowledgeFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7973, LogLevel.Warning, "GeoServer import job {JobId} rejected due to unsafe GeoServer URL {GeoServerUrl}")]
        public static partial void JobRejectedUnsafeServiceUrl(ILogger logger, string jobId, string geoServerUrl);
    }
}
