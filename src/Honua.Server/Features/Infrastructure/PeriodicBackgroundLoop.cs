// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure;

internal static class PeriodicBackgroundLoop
{
    internal static Task RunAsync(
        TimeSpan interval,
        Func<CancellationToken, Task> executeIterationAsync,
        Action<Exception> onIterationError,
        CancellationToken stoppingToken)
        => RunAsync(interval, TimeProvider.System, executeIterationAsync, onIterationError, stoppingToken);

    internal static async Task RunAsync(
        TimeSpan interval,
        TimeProvider timeProvider,
        Func<CancellationToken, Task> executeIterationAsync,
        Action<Exception> onIterationError,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(executeIterationAsync);
        ArgumentNullException.ThrowIfNull(onIterationError);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await executeIterationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            // Intentionally generic: long-running background polling loops must survive
            // transient single-iteration failures, log them via the caller callback, and
            // continue on the next interval.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onIterationError(ex);
            }

            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
