// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

// Drives the sweep where no in-process timer runs, such as a serverless host.
internal sealed class JobStoreRetentionScheduledTickHandler(
    JobStoreRetentionSweeper sweeper,
    SharedStateRetentionSweeper sharedState) : IScheduledTickHandler
{
    public ScheduledTickKind Kind => ScheduledTickKind.JobStoreRetention;

    public async Task RunTickAsync(CancellationToken cancellationToken = default)
    {
        await sweeper.SweepAsync(cancellationToken).ConfigureAwait(false);
        await sharedState.SweepAsync(cancellationToken).ConfigureAwait(false);
    }
}

// Deletes expired rows from every shared-state store, one bounded batch at a time.
internal sealed class SharedStateRetentionSweeper(IEnumerable<IExpiredRowSweep> stores, IOptions<JobStoreOptions> options)
{
    public async Task<long> SweepAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, options.Value.RetentionSweepBatchSize);
        long removed = 0;
        foreach (var store in stores)
        {
            int removedInBatch;
            do
            {
                removedInBatch = await store.DeleteExpiredAsync(batchSize, cancellationToken).ConfigureAwait(false);
                removed += removedInBatch;
            }
            while (removedInBatch == batchSize);
        }

        return removed;
    }
}

internal sealed partial class JobStoreRetentionSweepService(
    JobStoreRetentionSweeper sweeper,
    SharedStateRetentionSweeper sharedState,
    IOptions<JobStoreOptions> options,
    ILogger<JobStoreRetentionSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.RetentionSweepInterval);
            do
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown ends the sweep loop.
        }
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var removed = await sweeper.SweepAsync(stoppingToken).ConfigureAwait(false);
            if (removed.Total > 0)
            {
                LogSweepRemoved(logger, removed.Jobs, removed.LogStreams, removed.ResultPackages, removed.Leases);
            }

            var sharedStateRows = await sharedState.SweepAsync(stoppingToken).ConfigureAwait(false);
            if (sharedStateRows > 0)
            {
                LogSharedStateRemoved(logger, sharedStateRows);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        // One failed sweep must not stop the host; the next interval retries.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogSweepFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention sweep removed {Rows} expired shared state row(s).")]
    private static partial void LogSharedStateRemoved(ILogger logger, long rows);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job store retention sweep removed {Jobs} job(s), {LogStreams} log stream(s), {ResultPackages} result package(s) and {Leases} lease(s).")]
    private static partial void LogSweepRemoved(ILogger logger, long jobs, long logStreams, long resultPackages, long leases);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job store retention sweep failed; it runs again at the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
