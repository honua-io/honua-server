// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;

namespace Honua.Server.Features.Migration;

/// <summary>
/// Background service for processing Geoservices import jobs.
/// Uses Redis leader election when available, with a local fallback leader when Redis is unavailable.
/// </summary>
internal sealed partial class GeoservicesImportBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedImportJobManager _jobManager;
    private readonly IImportWorkerJobManager<GeoservicesImportRequest, GeoservicesImportProgress> _workerJobManager;
    private readonly ILogger<GeoservicesImportBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaderCheckInterval = TimeSpan.FromSeconds(10);

    public GeoservicesImportBackgroundService(
        IServiceScopeFactory scopeFactory,
        IDistributedImportJobManager jobManager,
        ILogger<GeoservicesImportBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _workerJobManager = jobManager as IImportWorkerJobManager<GeoservicesImportRequest, GeoservicesImportProgress>
            ?? new DistributedImportWorkerJobManagerAdapter(jobManager);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await ImportBackgroundServiceCoordinator.RunAsync(
            _workerJobManager,
            _logger,
            _pollInterval,
            _leaderCheckInterval,
            ProcessJobAsync,
            Log.ServiceStarting,
            Log.ServiceStopped,
            Log.NotLeader,
            Log.ProcessingError,
            stoppingToken).ConfigureAwait(false);

    private async Task ProcessJobAsync(string jobId, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.JobStarted(_logger, jobId);

        GeoservicesImportRequest? request = null;
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationTokenSource? monitorCancellation = null;
        Task? monitorTask = null;
        var acknowledgeCompletion = true;
        var leadershipLost = 0;
        using var progressController = new ImportJobProgressController<GeoservicesImportProgress>(
            jobId,
            _jobManager.ProgressStore,
            progressTtl: null,
            finalProgressTtl: TimeSpan.FromHours(24));

        try
        {
            var progress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken);
            progressController.Seed(progress);
            if (progress != null && IsTerminalStatus(progress.Status))
            {
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                if (progress.Status == GeoservicesImportStatus.Cancelled)
                {
                    Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                }

                return;
            }

            // Get the import request
            request = await _jobManager.RequestStore.GetProgressAsync(jobId, stoppingToken);
            if (request == null)
            {
                Log.JobRequestNotFound(_logger, jobId);

                if (progress == null ||
                    progress.Status is GeoservicesImportStatus.Completed or GeoservicesImportStatus.Failed or GeoservicesImportStatus.Cancelled)
                {
                    return;
                }

                var failedProgress = progress with
                {
                    Status = GeoservicesImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Import request not found.",
                    CurrentPhase = "Import request missing"
                };

                await progressController.SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                request = request with { JobId = jobId };
            }

            var serviceUrlValidation = await GeoservicesServiceUrlValidation.ValidateAsync(request.ServiceUrl, stoppingToken).ConfigureAwait(false);
            if (!serviceUrlValidation.IsValid)
            {
                var failedProgress = new GeoservicesImportProgress
                {
                    JobId = jobId,
                    Status = GeoservicesImportStatus.Failed,
                    SourceServiceUrl = request.ServiceUrl,
                    SourceLayerId = request.LayerId,
                    TableName = request.TableName,
                    StartedAt = progressController.CurrentProgress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = serviceUrlValidation.ErrorMessage,
                    CurrentPhase = "Import blocked by service URL validation"
                };

                await progressController.SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                Log.JobRejectedUnsafeServiceUrl(_logger, jobId, request.ServiceUrl);
                return;
            }

            // Update progress to processing
            progress = progressController.CurrentProgress;
            if (progress != null)
            {
                progress = progress with
                {
                    Status = GeoservicesImportStatus.Discovering,
                    CurrentPhase = "Discovering layer metadata"
                };
                await progressController.SetProgressAsync(progress, stoppingToken).ConfigureAwait(false);
            }

            // Create a scope for the import service
            using var scope = _scopeFactory.CreateScope();
            request = await GeoservicesCredentialResolution.ResolveSecretReferencesAsync(
                request,
                scope.ServiceProvider,
                jobCancellation.Token).ConfigureAwait(false);
            var importService = scope.ServiceProvider.GetRequiredService<IGeoservicesImportService>();

            monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            monitorTask = ImportBackgroundServiceCoordinator.MonitorCancellationAsync(
                jobId,
                _workerJobManager,
                jobCancellation,
                current => current.Status == GeoservicesImportStatus.Cancelled,
                () => Interlocked.Exchange(ref leadershipLost, 1),
                _logger,
                Log.LeadershipLostDuringJob,
                Log.CancellationMonitorPollFailed,
                monitorCancellation.Token);

            // Create progress reporter
            var progressReporter = new Progress<GeoservicesImportProgress>(p =>
                _ = progressController.TryReportProgressAsync(p, _logger, Log.ProgressUpdateFailed, CancellationToken.None));

            // Execute the import
            var result = await importService.ImportLayerAsync(request, progressReporter, jobCancellation.Token);

            stopwatch.Stop();

            // If the job was cancelled (the cancel endpoint writes Cancelled directly
            // to the progress store) while the import was completing, the cancellation
            // wins: don't overwrite the terminal Cancelled status with Completed/Failed.
            var storedProgress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (storedProgress?.Status == GeoservicesImportStatus.Cancelled)
            {
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                return;
            }

            // Update final progress
            var finalStatus = result.Success ? GeoservicesImportStatus.Completed : GeoservicesImportStatus.Failed;
            var finalProgress = new GeoservicesImportProgress
            {
                JobId = jobId,
                Status = finalStatus,
                FeaturesProcessed = result.FeatureCount,
                FailedFeatures = result.FailedFeatures,
                SourceServiceUrl = request.ServiceUrl,
                SourceLayerId = request.LayerId,
                SourceLayerName = result.SourceLayerName,
                TableName = request.TableName,
                ServiceName = result.ServiceName ?? request.ServiceName,
                PublishedLayerId = result.PublishedLayerId,
                StartedAt = progressController.CurrentProgress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = result.ErrorMessage,
                Warnings = result.Warnings,
                CurrentPhase = result.Success
                    ? result.PublishedLayerId.HasValue ? "Import completed and layer published" : "Import completed"
                    : "Import failed"
            };

            await progressController.SetFinalProgressAsync(finalProgress, stoppingToken).ConfigureAwait(false);

            // Clean up request store
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken);

            if (result.Success)
            {
                Log.JobCompleted(_logger, jobId, result.FeatureCount, stopwatch.Elapsed.TotalSeconds);
            }
            else
            {
                Log.JobFailed(_logger, jobId, result.ErrorMessage ?? "Unknown error", stopwatch.Elapsed.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            stopwatch.Stop();

            if (Volatile.Read(ref leadershipLost) != 0)
            {
                acknowledgeCompletion = false;
                var currentProgress = progressController.CurrentProgress;
                var resumableProgress = (currentProgress ?? GeoservicesImportProgress.CreateInitial(
                    jobId,
                    request?.ServiceUrl ?? string.Empty,
                    request?.LayerId ?? 0,
                    request?.TableName ?? string.Empty,
                    currentProgress?.SourceLayerName,
                    currentProgress?.EstimatedTotalFeatures)) with
                {
                    Status = GeoservicesImportStatus.Queued,
                    CompletedAt = null,
                    ErrorMessage = null,
                    CurrentPhase = "Queued for recovery after leadership loss"
                };

                await progressController.SetProgressAsync(resumableProgress, CancellationToken.None).ConfigureAwait(false);
                Log.JobRequeuedAfterLeadershipLoss(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            var currentCancelledProgress = progressController.CurrentProgress;
            var cancelledProgress = new GeoservicesImportProgress
            {
                JobId = jobId,
                Status = GeoservicesImportStatus.Cancelled,
                SourceServiceUrl = request?.ServiceUrl ?? currentCancelledProgress?.SourceServiceUrl ?? string.Empty,
                SourceLayerId = request?.LayerId ?? currentCancelledProgress?.SourceLayerId ?? 0,
                TableName = request?.TableName ?? currentCancelledProgress?.TableName ?? string.Empty,
                StartedAt = currentCancelledProgress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import cancelled"
            };

            await progressController.SetFinalProgressAsync(cancelledProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception) when (jobCancellation.IsCancellationRequested)
        {
            stopwatch.Stop();

            if (Volatile.Read(ref leadershipLost) != 0)
            {
                acknowledgeCompletion = false;
                var currentProgress = progressController.CurrentProgress;
                var resumableProgress = (currentProgress ?? GeoservicesImportProgress.CreateInitial(
                    jobId,
                    request?.ServiceUrl ?? string.Empty,
                    request?.LayerId ?? 0,
                    request?.TableName ?? string.Empty,
                    currentProgress?.SourceLayerName,
                    currentProgress?.EstimatedTotalFeatures)) with
                {
                    Status = GeoservicesImportStatus.Queued,
                    CompletedAt = null,
                    ErrorMessage = null,
                    CurrentPhase = "Queued for recovery after leadership loss"
                };

                await progressController.SetProgressAsync(resumableProgress, CancellationToken.None).ConfigureAwait(false);
                Log.JobRequeuedAfterLeadershipLoss(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            var currentCancelledProgress = progressController.CurrentProgress;
            var cancelledProgress = new GeoservicesImportProgress
            {
                JobId = jobId,
                Status = GeoservicesImportStatus.Cancelled,
                SourceServiceUrl = request?.ServiceUrl ?? currentCancelledProgress?.SourceServiceUrl ?? string.Empty,
                SourceLayerId = request?.LayerId ?? currentCancelledProgress?.SourceLayerId ?? 0,
                TableName = request?.TableName ?? currentCancelledProgress?.TableName ?? string.Empty,
                StartedAt = currentCancelledProgress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import cancelled"
            };

            await progressController.SetFinalProgressAsync(cancelledProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var currentFailedProgress = progressController.CurrentProgress;
            var failedProgress = new GeoservicesImportProgress
            {
                JobId = jobId,
                Status = GeoservicesImportStatus.Failed,
                SourceServiceUrl = request?.ServiceUrl ?? currentFailedProgress?.SourceServiceUrl ?? string.Empty,
                SourceLayerId = request?.LayerId ?? currentFailedProgress?.SourceLayerId ?? 0,
                TableName = request?.TableName ?? currentFailedProgress?.TableName ?? string.Empty,
                StartedAt = currentFailedProgress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Import failed.",
                CurrentPhase = "Import failed with exception"
            };

            await progressController.SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            Log.JobException(_logger, jobId, ex);
        }
        finally
        {
            await ImportBackgroundServiceCoordinator.AcknowledgeCompletionAsync(
                _jobManager.JobQueue,
                jobId,
                acknowledgeCompletion,
                _logger,
                Log.JobCompletionAcknowledgeFailed).ConfigureAwait(false);

            await ImportBackgroundServiceCoordinator.StopMonitorAsync(monitorCancellation, monitorTask).ConfigureAwait(false);
        }
    }

    private static bool IsTerminalStatus(GeoservicesImportStatus status)
        => status is GeoservicesImportStatus.Completed or GeoservicesImportStatus.Failed or GeoservicesImportStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(7700, LogLevel.Information, "Geoservices import background service starting (instance: {InstanceId})")]
        public static partial void ServiceStarting(ILogger logger, string instanceId);

        [LoggerMessage(7701, LogLevel.Information, "Geoservices import background service stopped (instance: {InstanceId})")]
        public static partial void ServiceStopped(ILogger logger, string instanceId);

        [LoggerMessage(7702, LogLevel.Debug, "Instance {InstanceId} is not the leader, waiting...")]
        public static partial void NotLeader(ILogger logger, string instanceId);

        [LoggerMessage(7703, LogLevel.Information, "Processing import job {JobId}")]
        public static partial void JobStarted(ILogger logger, string jobId);

        [LoggerMessage(7704, LogLevel.Information, "Import job {JobId} completed: {FeatureCount} features in {DurationSeconds:F1}s")]
        public static partial void JobCompleted(ILogger logger, string jobId, int featureCount, double durationSeconds);

        [LoggerMessage(7705, LogLevel.Warning, "Import job {JobId} failed: {ErrorMessage} (duration: {DurationSeconds:F1}s)")]
        public static partial void JobFailed(ILogger logger, string jobId, string errorMessage, double durationSeconds);

        [LoggerMessage(7706, LogLevel.Warning, "Import job {JobId} cancelled (duration: {DurationSeconds:F1}s)")]
        public static partial void JobCancelled(ILogger logger, string jobId, double durationSeconds);

        [LoggerMessage(7707, LogLevel.Error, "Import job {JobId} threw exception")]
        public static partial void JobException(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7708, LogLevel.Warning, "Import job {JobId} request not found in store")]
        public static partial void JobRequestNotFound(ILogger logger, string jobId);

        [LoggerMessage(7709, LogLevel.Warning, "Failed to update progress for job {JobId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7710, LogLevel.Error, "Error in background processing loop")]
        public static partial void ProcessingError(ILogger logger, Exception exception);

        [LoggerMessage(7711, LogLevel.Debug, "Cancellation monitor poll failed for job {JobId}")]
        public static partial void CancellationMonitorPollFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7712, LogLevel.Warning, "Import job {JobId} blocked by service URL validation: {ServiceUrl}")]
        public static partial void JobRejectedUnsafeServiceUrl(ILogger logger, string jobId, string serviceUrl);

        [LoggerMessage(7713, LogLevel.Warning, "Failed to acknowledge completion for import job {JobId}")]
        public static partial void JobCompletionAcknowledgeFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7714, LogLevel.Warning, "Leadership was lost while processing import job {JobId}; cancelling local execution.")]
        public static partial void LeadershipLostDuringJob(ILogger logger, string jobId);

        [LoggerMessage(7715, LogLevel.Warning, "Import job {JobId} yielded after leadership loss and will be recovered by the next durable leader (duration: {DurationSeconds:F1}s)")]
        public static partial void JobRequeuedAfterLeadershipLoss(ILogger logger, string jobId, double durationSeconds);
    }
}
