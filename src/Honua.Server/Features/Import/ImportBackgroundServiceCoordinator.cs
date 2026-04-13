// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Server.Features.Import;

internal interface IImportWorkerJobManager<TRequest, TProgress> : IImportCoordinationHealth
    where TRequest : class
    where TProgress : class
{
    IDistributedJobQueueService JobQueue { get; }

    IDistributedLeaderElection LeaderElection { get; }

    IDistributedProgressStore<TProgress> ProgressStore { get; }

    IDistributedProgressStore<TRequest> RequestStore { get; }
}

internal sealed class DistributedImportWorkerJobManagerAdapter : IImportWorkerJobManager<GeoservicesImportRequest, GeoservicesImportProgress>
{
    private readonly IDistributedImportJobManager _jobManager;

    public DistributedImportWorkerJobManagerAdapter(IDistributedImportJobManager jobManager)
    {
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
    }

    public IDistributedJobQueueService JobQueue => _jobManager.JobQueue;

    public IDistributedLeaderElection LeaderElection => _jobManager.LeaderElection;

    public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore => _jobManager.ProgressStore;

    public IDistributedProgressStore<GeoservicesImportRequest> RequestStore => _jobManager.RequestStore;

    public bool CanAcceptNewJobs => _jobManager is not IImportCoordinationHealth { CanAcceptNewJobs: false };
}

internal static class ImportBackgroundServiceCoordinator
{
    private static readonly TimeSpan _processingErrorDelay = TimeSpan.FromSeconds(5);
    private const string ImportWorkerInstanceIdKey = "ImportWorkerInstanceId";
    private const string ImportJobIdKey = "ImportJobId";

    public static async Task RunAsync<TRequest, TProgress>(
        IImportWorkerJobManager<TRequest, TProgress> jobManager,
        ILogger logger,
        TimeSpan pollInterval,
        TimeSpan leaderCheckInterval,
        Func<string, CancellationToken, Task> processJobAsync,
        Action<ILogger, string> logServiceStarting,
        Action<ILogger, string> logServiceStopped,
        Action<ILogger, string> logNotLeader,
        Action<ILogger, Exception> logProcessingError,
        CancellationToken stoppingToken)
        where TRequest : class
        where TProgress : class
    {
        using var serviceScope = BeginWorkerScope(logger, jobManager.LeaderElection.InstanceId);
        logServiceStarting(logger, jobManager.LeaderElection.InstanceId);
        var recoveredInFlightJobs = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var isLeader = await jobManager.LeaderElection.TryAcquireLeadershipAsync(stoppingToken).ConfigureAwait(false);
                if (!isLeader)
                {
                    recoveredInFlightJobs = false;
                    logNotLeader(logger, jobManager.LeaderElection.InstanceId);
                    await Task.Delay(leaderCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var heartbeatMaintained = await jobManager.LeaderElection.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
                if (!heartbeatMaintained)
                {
                    recoveredInFlightJobs = false;
                    logNotLeader(logger, jobManager.LeaderElection.InstanceId);
                    await Task.Delay(leaderCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!jobManager.CanAcceptNewJobs)
                {
                    recoveredInFlightJobs = false;
                    await Task.Delay(leaderCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!recoveredInFlightJobs)
                {
                    await jobManager.JobQueue.RecoverInFlightAsync(stoppingToken).ConfigureAwait(false);
                    recoveredInFlightJobs = true;
                }

                var jobId = await jobManager.JobQueue.DequeueAsync(pollInterval, stoppingToken).ConfigureAwait(false);
                if (jobId != null)
                {
                    using var jobScope = BeginJobScope(logger, jobManager.LeaderElection.InstanceId, jobId);
                    await processJobAsync(jobId, stoppingToken).ConfigureAwait(false);
                    if (!jobManager.LeaderElection.IsLeader || !jobManager.CanAcceptNewJobs)
                    {
                        recoveredInFlightJobs = false;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logProcessingError(logger, ex);
                await Task.Delay(_processingErrorDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        await jobManager.LeaderElection.ReleaseLeadershipAsync(CancellationToken.None).ConfigureAwait(false);
        logServiceStopped(logger, jobManager.LeaderElection.InstanceId);
    }

    public static async Task MonitorCancellationAsync<TRequest, TProgress>(
        string jobId,
        IImportWorkerJobManager<TRequest, TProgress> jobManager,
        CancellationTokenSource jobCancellation,
        Func<TProgress, bool> isCancelled,
        Action markLeadershipLost,
        ILogger logger,
        Action<ILogger, string> logLeadershipLost,
        Action<ILogger, string, Exception> logPollFailed,
        CancellationToken token)
        where TRequest : class
        where TProgress : class
    {
        using var monitorScope = BeginJobScope(logger, jobManager.LeaderElection.InstanceId, jobId);
        while (!token.IsCancellationRequested && !jobCancellation.IsCancellationRequested)
        {
            try
            {
                var current = await jobManager.ProgressStore.GetProgressAsync(jobId, token).ConfigureAwait(false);
                if (current != null && isCancelled(current))
                {
                    jobCancellation.Cancel();
                    return;
                }

                var leadershipMaintained = await jobManager.LeaderElection.HeartbeatAsync(token).ConfigureAwait(false);
                if (!leadershipMaintained)
                {
                    markLeadershipLost();
                    logLeadershipLost(logger, jobId);
                    jobCancellation.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logPollFailed(logger, jobId, ex);
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

    private static IDisposable? BeginWorkerScope(ILogger logger, string instanceId)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            [ImportWorkerInstanceIdKey] = instanceId
        });
    }

    private static IDisposable? BeginJobScope(ILogger logger, string instanceId, string jobId)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            [ImportWorkerInstanceIdKey] = instanceId,
            [ImportJobIdKey] = jobId
        });
    }

    public static async Task AcknowledgeCompletionAsync(
        IDistributedJobQueueService jobQueue,
        string jobId,
        bool acknowledgeCompletion,
        ILogger logger,
        Action<ILogger, string, Exception> logCompletionAcknowledgeFailed)
    {
        if (!acknowledgeCompletion)
        {
            return;
        }

        try
        {
            await jobQueue.CompleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logCompletionAcknowledgeFailed(logger, jobId, ex);
        }
    }

    public static async Task StopMonitorAsync(CancellationTokenSource? monitorCancellation, Task? monitorTask)
    {
        if (monitorCancellation == null)
        {
            return;
        }

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

internal sealed class ImportJobProgressController<TProgress> : IDisposable
    where TProgress : class
{
    private readonly string _jobId;
    private readonly IDistributedProgressStore<TProgress> _progressStore;
    private readonly TimeSpan? _progressTtl;
    private readonly TimeSpan? _finalProgressTtl;
    private readonly SemaphoreSlim _progressGate = new(1, 1);
    private int _finalized;

    public ImportJobProgressController(
        string jobId,
        IDistributedProgressStore<TProgress> progressStore,
        TimeSpan? progressTtl = null,
        TimeSpan? finalProgressTtl = null)
    {
        _jobId = jobId;
        _progressStore = progressStore;
        _progressTtl = progressTtl;
        _finalProgressTtl = finalProgressTtl ?? progressTtl;
    }

    public TProgress? CurrentProgress { get; private set; }

    public bool IsFinalized => Volatile.Read(ref _finalized) != 0;

    public void Seed(TProgress? progress)
    {
        CurrentProgress = progress;
    }

    public async Task SetProgressAsync(TProgress progress, CancellationToken cancellationToken)
    {
        if (IsFinalized)
        {
            return;
        }

        await _progressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsFinalized)
            {
                return;
            }

            CurrentProgress = progress;
            await _progressStore.SetProgressAsync(_jobId, progress, _progressTtl, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }
    }

    public async Task SetFinalProgressAsync(TProgress progress, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _finalized, 1);
        await _progressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            CurrentProgress = progress;
            await _progressStore.SetProgressAsync(_jobId, progress, _finalProgressTtl, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }
    }

    public async Task TryReportProgressAsync(
        TProgress progress,
        ILogger logger,
        Action<ILogger, string, Exception> logProgressUpdateFailed,
        CancellationToken jobCancellationToken)
    {
        try
        {
            if (jobCancellationToken.IsCancellationRequested || IsFinalized)
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await SetProgressAsync(progress, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logProgressUpdateFailed(logger, _jobId, ex);
        }
    }

    public void Dispose()
    {
        _progressGate.Dispose();
    }
}
