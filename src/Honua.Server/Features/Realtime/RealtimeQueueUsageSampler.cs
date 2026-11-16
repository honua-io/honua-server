// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;

namespace Honua.Server.Features.Realtime;

// Only the maintenance lease holder samples the database-wide notification queue. The lease
// outlives the one-minute interval so nodes cannot alternate probes in the same interval.
internal sealed partial class RealtimeQueueUsageSampler(
    ILeaseStore? leases,
    Func<CancellationToken, Task<double>> readUsage,
    ILogger<RealtimeQueueUsageSampler> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(75);
    private readonly LeaseClaim _claim = new("realtime-maintenance:notification-queue", Guid.NewGuid().ToString("N"));
    private bool _held;

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval, Clock);
            do
            {
                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown ends sampling.
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    internal async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        if (leases is null)
        {
            return;
        }

        try
        {
            _held = _held
                ? await leases.RenewAsync(_claim, _leaseDuration, cancellationToken).ConfigureAwait(false)
                : await leases.TryAcquireAsync(_claim, _leaseDuration, cancellationToken).ConfigureAwait(false);
            if (_held)
            {
                RealtimeMetrics.NotifyQueueUsage.Record(await readUsage(cancellationToken).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogSampleFailed(logger, exception);
        }
    }

    private async Task ReleaseAsync()
    {
        if (!_held || leases is null)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await leases.ReleaseAsync(_claim, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogSampleFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime notification queue sample failed; the next minute retries.")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
