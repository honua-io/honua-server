// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsSelfCheckSampler(
    OpsSelfChecks checks,
    TimeProvider clock,
    ILogger<OpsSelfCheckSampler>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await checks.EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (logger is not null)
                    {
                        LogEvaluationFailed(logger, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown interrupts a pending timer or outbound check.
        }
    }

    [LoggerMessage(
        EventId = 9498,
        Level = LogLevel.Error,
        Message = "Scheduled ops self-check evaluation failed; continuing on next interval.")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception exception);
}
