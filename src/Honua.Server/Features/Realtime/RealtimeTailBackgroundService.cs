// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// Follows the realtime log on a serving task (specification section 6.3): probe once at start,
// then on every wake-up (coalesced) and at the poll interval. With a listener the poll is the
// safety poll while it is connected and the degraded poll while it is not; without one (the
// in-process provider) it is the local poll. Presence is flushed on its own interval.
internal sealed partial class RealtimeTailBackgroundService(
    RealtimeDispatcher? dispatcher,
    RealtimePresenceBatcher presence,
    RealtimeListener? listener,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeTailBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A host without a system database has no log to follow.
        if (dispatcher is not { } active)
        {
            return;
        }

        var listening = listener is null
            ? Task.CompletedTask
            : listener.RunAsync(active.Wake, stoppingToken);
        var flushing = FlushPresenceAsync(active, stoppingToken);
        try
        {
            await FollowAsync(active, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }

        await Task.WhenAll(listening, flushing).ConfigureAwait(false);
    }

    internal TimeSpan CurrentPollInterval()
    {
        var current = options.Value;
        if (listener is null)
        {
            return current.LocalPollInterval;
        }

        return listener.IsConnected ? current.SafetyPollInterval : current.DegradedPollInterval;
    }

    private async Task FollowAsync(RealtimeDispatcher active, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await active.DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            // A failed probe or read is retried at the next wake-up or poll; positions live in the
            // database, so nothing is lost.
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogDispatchFailed(logger, exception);
            }

            if (await active.WaitForWakeAsync(CurrentPollInterval(), stoppingToken).ConfigureAwait(false))
            {
                var coalesce = options.Value.WakeCoalesceWindow;
                if (coalesce > TimeSpan.Zero)
                {
                    await Task.Delay(coalesce, stoppingToken).ConfigureAwait(false);
                }

                active.DrainWakeups();
            }
        }
    }

    private async Task FlushPresenceAsync(RealtimeDispatcher active, CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.PresenceFlushInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (await presence.FlushAsync(stoppingToken).ConfigureAwait(false) > 0)
                    {
                        active.Wake();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                // Presence is best effort: a failed flush drops that batch.
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LogPresenceFlushFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime dispatch failed; it runs again at the next wake-up or poll.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A realtime presence batch could not be appended and was dropped.")]
    private static partial void LogPresenceFlushFailed(ILogger logger, Exception exception);
}
