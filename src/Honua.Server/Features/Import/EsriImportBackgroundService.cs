// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Import.Abstractions;

namespace Honua.Server.Features.Import;

/// <summary>
/// Background service for processing Esri import jobs.
/// Uses distributed leader election to ensure only one instance processes jobs at a time.
/// </summary>
internal sealed partial class EsriImportBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedImportJobManager _jobManager;
    private readonly ILogger<EsriImportBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaderCheckInterval = TimeSpan.FromSeconds(10);

    public EsriImportBackgroundService(
        IServiceScopeFactory scopeFactory,
        IDistributedImportJobManager jobManager,
        ILogger<EsriImportBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.ServiceStarting(_logger, _jobManager.LeaderElection.InstanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to acquire or maintain leadership
                var isLeader = _jobManager.LeaderElection.IsLeader ||
                               await _jobManager.LeaderElection.TryAcquireLeadershipAsync(stoppingToken);

                if (!isLeader)
                {
                    Log.NotLeader(_logger, _jobManager.LeaderElection.InstanceId);
                    await Task.Delay(_leaderCheckInterval, stoppingToken);
                    continue;
                }

                // Send heartbeat to maintain leadership
                await _jobManager.LeaderElection.HeartbeatAsync(stoppingToken);

                // Try to dequeue and process a job
                var jobId = await _jobManager.JobQueue.DequeueAsync(_pollInterval, stoppingToken);

                if (jobId != null)
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ProcessingError(_logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        // Release leadership on shutdown
        await _jobManager.LeaderElection.ReleaseLeadershipAsync(CancellationToken.None);
        Log.ServiceStopped(_logger, _jobManager.LeaderElection.InstanceId);
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.JobStarted(_logger, jobId);

        try
        {
            // Get the import request
            var request = await _jobManager.RequestStore.GetProgressAsync(jobId, stoppingToken);
            if (request == null)
            {
                Log.JobRequestNotFound(_logger, jobId);
                return;
            }

            // Update progress to processing
            var progress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken);
            if (progress != null)
            {
                progress = progress with
                {
                    Status = EsriImportStatus.Discovering,
                    CurrentPhase = "Discovering layer metadata"
                };
                await _jobManager.ProgressStore.SetProgressAsync(jobId, progress, cancellationToken: stoppingToken);
            }

            // Create a scope for the import service
            using var scope = _scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IEsriImportService>();

            // Create progress reporter
            var progressReporter = new Progress<EsriImportProgress>(async p =>
            {
                try
                {
                    await _jobManager.ProgressStore.SetProgressAsync(jobId, p, cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.ProgressUpdateFailed(_logger, jobId, ex);
                }
            });

            // Execute the import
            var result = await importService.ImportLayerAsync(request, progressReporter, stoppingToken);

            stopwatch.Stop();

            // Update final progress
            var finalStatus = result.Success ? EsriImportStatus.Completed : EsriImportStatus.Failed;
            var finalProgress = new EsriImportProgress
            {
                JobId = jobId,
                Status = finalStatus,
                FeaturesProcessed = result.FeatureCount,
                FailedFeatures = result.FailedFeatures,
                SourceServiceUrl = request.ServiceUrl,
                SourceLayerId = request.LayerId,
                TableName = request.TableName,
                StartedAt = progress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = result.ErrorMessage,
                Warnings = result.Warnings,
                CurrentPhase = result.Success ? "Import completed" : "Import failed"
            };

            await _jobManager.ProgressStore.SetProgressAsync(jobId, finalProgress,
                TimeSpan.FromHours(24), stoppingToken);

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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            var cancelledProgress = new EsriImportProgress
            {
                JobId = jobId,
                Status = EsriImportStatus.Cancelled,
                SourceServiceUrl = "",
                SourceLayerId = 0,
                TableName = "",
                StartedAt = DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import cancelled"
            };

            await _jobManager.ProgressStore.SetProgressAsync(jobId, cancelledProgress,
                TimeSpan.FromHours(24), CancellationToken.None);

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var failedProgress = new EsriImportProgress
            {
                JobId = jobId,
                Status = EsriImportStatus.Failed,
                SourceServiceUrl = "",
                SourceLayerId = 0,
                TableName = "",
                StartedAt = DateTimeOffset.UtcNow.Subtract(stopwatch.Elapsed),
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Import failed.",
                CurrentPhase = "Import failed with exception"
            };

            await _jobManager.ProgressStore.SetProgressAsync(jobId, failedProgress,
                TimeSpan.FromHours(24), CancellationToken.None);

            Log.JobException(_logger, jobId, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7700, LogLevel.Information, "Esri import background service starting (instance: {InstanceId})")]
        public static partial void ServiceStarting(ILogger logger, string instanceId);

        [LoggerMessage(7701, LogLevel.Information, "Esri import background service stopped (instance: {InstanceId})")]
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
    }
}
