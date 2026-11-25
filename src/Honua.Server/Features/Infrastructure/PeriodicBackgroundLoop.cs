// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure;

internal sealed class PeriodicBackgroundLoopOptions
{
    public required TimeSpan Interval { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public required Func<CancellationToken, Task> ExecuteIterationAsync { get; init; }
    public required Action<Exception> OnIterationError { get; init; }
}

internal static class PeriodicBackgroundLoop
{
    internal static async Task RunAsync(
        PeriodicBackgroundLoopOptions options,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentNullException.ThrowIfNull(options.ExecuteIterationAsync);
        ArgumentNullException.ThrowIfNull(options.OnIterationError);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await options.ExecuteIterationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                options.OnIterationError(ex);
            }

            try
            {
                await Task.Delay(options.Interval, options.TimeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
