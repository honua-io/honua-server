// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Server.Features.Migration;

/// <summary>
/// Background worker that processes queued migration evidence jobs.
/// </summary>
internal sealed partial class MigrationEvidenceBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MigrationEvidenceJobManager _jobManager;
    private readonly ILogger<MigrationEvidenceBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaderCheckInterval = TimeSpan.FromSeconds(10);

    public MigrationEvidenceBackgroundService(
        IServiceScopeFactory scopeFactory,
        MigrationEvidenceJobManager jobManager,
        ILogger<MigrationEvidenceBackgroundService> logger)
    {
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
                    await Task.Delay(_leaderCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var heartbeatMaintained = await _jobManager.LeaderElection.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
                if (!heartbeatMaintained)
                {
                    recoveredInFlightJobs = false;
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
        MigrationEvidenceRequest? request = null;
        MigrationEvidenceProgress? progress = null;
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationTokenSource? monitorCancellation = null;
        Task? monitorTask = null;

        async Task SetProgressAsync(MigrationEvidenceProgress update, CancellationToken token)
        {
            progress = update;
            await _jobManager.ProgressStore.SetProgressAsync(jobId, update, TimeSpan.FromHours(24), token).ConfigureAwait(false);
        }

        async Task MonitorCancellationAsync(string id, CancellationTokenSource jobCts, CancellationToken token)
        {
            while (!token.IsCancellationRequested && !jobCts.IsCancellationRequested)
            {
                try
                {
                    var current = await _jobManager.ProgressStore.GetProgressAsync(id, token).ConfigureAwait(false);
                    if (current?.Status == MigrationEvidenceJobStatus.Cancelled)
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
            Log.JobStarted(_logger, jobId);
            progress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (progress?.Status == MigrationEvidenceJobStatus.Cancelled)
            {
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            request = await _jobManager.RequestStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (request == null)
            {
                Log.JobRequestNotFound(_logger, jobId);
                if (progress is not null)
                {
                    await SetProgressAsync(progress with
                    {
                        Status = MigrationEvidenceJobStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = "Evidence request not found.",
                        CurrentPhase = "Evidence request missing"
                    }, CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }

            monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            monitorTask = MonitorCancellationAsync(jobId, jobCancellation, monitorCancellation.Token);

            await SetProgressAsync(
                (progress ?? MigrationEvidenceProgress.CreateInitial(jobId, request)) with
                {
                    Status = MigrationEvidenceJobStatus.ResolvingSourceBaseline,
                    CompletedSteps = 1,
                    CurrentPhase = "Resolving source baseline"
                },
                CancellationToken.None).ConfigureAwait(false);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var generator = scope.ServiceProvider.GetRequiredService<IMigrationEvidenceGenerator>();
            var reportStore = scope.ServiceProvider.GetRequiredService<IMigrationEvidenceReportStore>();

            var report = await generator.GenerateAsync(request, jobCancellation.Token).ConfigureAwait(false);
            var latestProgress = await _jobManager.ProgressStore.GetProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (latestProgress?.Status == MigrationEvidenceJobStatus.Cancelled)
            {
                jobCancellation.Cancel();
                throw new OperationCanceledException(jobCancellation.Token);
            }

            await SetProgressAsync(progress! with
            {
                Status = MigrationEvidenceJobStatus.PersistingReport,
                CompletedSteps = 3,
                CurrentPhase = "Persisting immutable report artifact"
            }, CancellationToken.None).ConfigureAwait(false);

            await reportStore.StoreAsync(report, jobCancellation.Token).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, jobCancellation.Token).ConfigureAwait(false);

            await SetProgressAsync(progress! with
            {
                Status = MigrationEvidenceJobStatus.Completed,
                CompletedSteps = progress.TotalSteps,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Report generated",
                ReportId = report.ReportId,
                Readiness = report.CutoverReadiness.State,
                Warnings = report.CutoverReadiness.Warnings
            }, CancellationToken.None).ConfigureAwait(false);

            Log.JobCompleted(_logger, jobId, report.ReportId, report.CutoverReadiness.State, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            if (progress != null)
            {
                await SetProgressAsync(progress with
                {
                    Status = MigrationEvidenceJobStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Evidence generation cancelled"
                }, CancellationToken.None).ConfigureAwait(false);
            }

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            if (progress != null)
            {
                await SetProgressAsync(progress with
                {
                    Status = MigrationEvidenceJobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message,
                    CurrentPhase = "Evidence generation failed"
                }, CancellationToken.None).ConfigureAwait(false);
            }

            Log.JobFailed(_logger, jobId, ex.Message, ex);
        }
        finally
        {
            stopwatch.Stop();
            if (monitorCancellation != null)
            {
                await monitorCancellation.CancelAsync().ConfigureAwait(false);
                if (monitorTask != null)
                {
                    try
                    {
                        await monitorTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            await _jobManager.JobQueue.CompleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9130, LogLevel.Information, "Migration evidence background service starting on {InstanceId}.")]
        public static partial void ServiceStarting(ILogger logger, string instanceId);

        [LoggerMessage(9131, LogLevel.Information, "Migration evidence background service stopped on {InstanceId}.")]
        public static partial void ServiceStopped(ILogger logger, string instanceId);

        [LoggerMessage(9132, LogLevel.Warning, "Migration evidence background service processing loop failed.")]
        public static partial void ProcessingError(ILogger logger, Exception exception);

        [LoggerMessage(9133, LogLevel.Information, "Migration evidence job {JobId} started.")]
        public static partial void JobStarted(ILogger logger, string jobId);

        [LoggerMessage(9134, LogLevel.Warning, "Migration evidence request payload missing for job {JobId}.")]
        public static partial void JobRequestNotFound(ILogger logger, string jobId);

        [LoggerMessage(9135, LogLevel.Warning, "Migration evidence cancellation monitor failed for job {JobId}.")]
        public static partial void CancellationMonitorPollFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(9136, LogLevel.Information, "Migration evidence job {JobId} completed with report {ReportId} and readiness {Readiness} in {DurationSeconds}s.")]
        public static partial void JobCompleted(ILogger logger, string jobId, Guid reportId, MigrationReadinessState readiness, double durationSeconds);

        [LoggerMessage(9137, LogLevel.Information, "Migration evidence job {JobId} cancelled after {DurationSeconds}s.")]
        public static partial void JobCancelled(ILogger logger, string jobId, double durationSeconds);

        [LoggerMessage(9138, LogLevel.Warning, "Migration evidence job {JobId} failed: {Message}")]
        public static partial void JobFailed(ILogger logger, string jobId, string message, Exception exception);
    }
}
