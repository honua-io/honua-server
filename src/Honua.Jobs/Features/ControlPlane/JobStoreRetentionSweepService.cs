// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal sealed partial class JobStoreRetentionSweepService(
    JobStoreRetentionSweeper sweeper,
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job store retention sweep removed {Jobs} job(s), {LogStreams} log stream(s), {ResultPackages} result package(s) and {Leases} lease(s).")]
    private static partial void LogSweepRemoved(ILogger logger, long jobs, long logStreams, long resultPackages, long leases);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job store retention sweep failed; it runs again at the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
