// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// Writes Realtime:Retention:<family> to realtime_log_heads after startup, so the head rows are the
// only place retention is read from. The tables may not exist yet (degraded start, or migrations run
// by a separate job), so the write is retried with capped backoff until it succeeds once; host start
// never waits for it. A host without a system database has no log.
internal sealed partial class RealtimeRetentionStartupService(
    PostgresRealtimeEventLog? log,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeRetentionStartupService> logger) : BackgroundService
{
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    // Completes when the configured retention has been written (or there is no log).
    internal Task Written => _written.Task;

    private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (log is null)
        {
            _written.TrySetResult();
            return;
        }

        var retention = options.Value.LoggedRetention();
        var delay = FirstRetryDelay;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WriteRetentionAsync(retention, stoppingToken).ConfigureAwait(false);
                _written.TrySetResult();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Warn once, then quietly keep trying: the stored retention stays in force meanwhile.
                if (failures++ == 0)
                {
                    LogRetentionFailed(logger, exception);
                }
            }

            if (!await WaitForRetryAsync(delay, stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    private async Task WriteRetentionAsync(IReadOnlyDictionary<string, TimeSpan> retention, CancellationToken cancellationToken)
    {
        var changed = await log!.ApplyRetentionAsync(retention, cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            LogRetentionWritten(logger, changed);
        }
    }

    private static async Task<bool> WaitForRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Realtime retention updated for {Families} event family(ies).")]
    private static partial void LogRetentionWritten(ILogger logger, int families);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime retention could not be written yet; retrying in the background. The stored retention stays in force meanwhile.")]
    private static partial void LogRetentionFailed(ILogger logger, Exception exception);
}
