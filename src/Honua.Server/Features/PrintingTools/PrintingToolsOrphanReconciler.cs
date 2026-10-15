// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Stable id for the print worker in this process. A restart mints a new id, so
/// jobs still heartbeating under the previous id are not treated as local.
/// </summary>
internal sealed class PrintWorkerInstance
{
    /// <summary>Creates a new instance id.</summary>
    public PrintWorkerInstance() => InstanceId = Guid.NewGuid().ToString("N");

    /// <summary>Id stamped onto print progress this process accepts.</summary>
    public string InstanceId { get; }
}

/// <summary>
/// Job ids this process has accepted and not yet finished. The orphan sweep
/// refreshes heartbeats only for these ids.
/// </summary>
internal sealed class PrintJobTracker
{
    private readonly ConcurrentDictionary<string, byte> _jobs = new(StringComparer.Ordinal);

    /// <summary>Remember a job this process still has to run or is running.</summary>
    public void Track(string jobId) => _jobs.TryAdd(jobId, 0);

    /// <summary>Drop a job that finished, was rejected, or is no longer ours.</summary>
    public void Forget(string jobId) => _jobs.TryRemove(jobId, out _);

    /// <summary>True when this process still claims the job.</summary>
    public bool Contains(string jobId) => _jobs.ContainsKey(jobId);

    /// <summary>Snapshot of tracked ids.</summary>
    public IReadOnlyCollection<string> Snapshot() => _jobs.Keys.ToArray();
}

/// <summary>
/// How often to look for print jobs whose worker stopped, and how long a
/// heartbeat may stay quiet before the job is failed.
/// </summary>
internal sealed class PrintingToolsOrphanOptions
{
    /// <summary>Time between orphan sweeps. Default is 15 seconds.</summary>
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Age past which a Queued or Processing print with no heartbeat is failed.
    /// Must exceed <see cref="ReconciliationInterval"/>. Default is 60 seconds.
    /// </summary>
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Fails Queued or Processing print jobs whose owner has stopped heartbeating.
/// The in-process channel does not survive a restart; clients otherwise poll
/// esriJobSubmitted until the 24 hour progress TTL.
/// </summary>
internal sealed class PrintingToolsOrphanReconciler(
    IUniversalProgressStore progressStore,
    PrintWorkerInstance instance,
    PrintJobTracker tracker,
    IOptions<PrintingToolsOrphanOptions> options,
    ILogger<PrintingToolsOrphanReconciler> logger) : BackgroundService
{
    internal const string RestartedMessage = "print worker restarted; resubmit";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.Value.ReconciliationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown. Jobs this process still tracks stay heartbeated until
            // the lease elapses and another instance fails them.
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshOwnedHeartbeatsAsync(cancellationToken).ConfigureAwait(false);
            await FailStaleJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A store blip must not stop the hosted sweep. The next tick retries.
            PrintingToolsLog.OrphanSweepFailed(logger, ex);
        }
    }

    private async Task RefreshOwnedHeartbeatsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var jobId in tracker.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await progressStore.GetProgressAsync<PrintProgress>(jobId, cancellationToken).ConfigureAwait(false)
                is not PrintProgress progress)
            {
                tracker.Forget(jobId);
                continue;
            }

            if (IsTerminal(progress.Status)
                || !string.Equals(progress.OwnerInstanceId, instance.InstanceId, StringComparison.Ordinal))
            {
                tracker.Forget(jobId);
                continue;
            }

            var refreshed = progress with { HeartbeatAt = now };
            var result = await progressStore.TrySetProgressAsync(
                    jobId,
                    refreshed,
                    progress.Status,
                    PrintingToolsRequestHandlers.ResultTtl,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == ProgressCompareAndSetOutcome.NotFound)
            {
                tracker.Forget(jobId);
            }
        }
    }

    private async Task FailStaleJobsAsync(CancellationToken cancellationToken)
    {
        var active = await progressStore
            .GetActiveOperationsAsync<PrintProgress>(OperationType.Print, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var lease = options.Value.LeaseTimeout;

        foreach (var progress in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (progress.Status is not (OperationStatus.Queued or OperationStatus.Processing))
            {
                continue;
            }

            // This process still has the job. A slow render must not be failed
            // between heartbeats, and a sibling replica's fresh heartbeat must
            // be left alone.
            if (string.Equals(progress.OwnerInstanceId, instance.InstanceId, StringComparison.Ordinal)
                && tracker.Contains(progress.JobId))
            {
                continue;
            }

            var beat = progress.HeartbeatAt ?? progress.StartedAt;
            if (now - beat <= lease)
            {
                continue;
            }

            var failed = progress with
            {
                Status = OperationStatus.Failed,
                ErrorMessage = RestartedMessage,
                CurrentPhase = "Failed",
                CompletedAt = now,
                DownloadUrl = null
            };
            var result = await progressStore.TrySetProgressAsync(
                    progress.JobId,
                    failed,
                    progress.Status,
                    PrintingToolsRequestHandlers.ResultTtl,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == ProgressCompareAndSetOutcome.Updated)
            {
                tracker.Forget(progress.JobId);
                PrintingToolsLog.OrphanedPrintFailed(logger, progress.JobId);
            }
        }
    }

    private static bool IsTerminal(OperationStatus status)
        => status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;
}
