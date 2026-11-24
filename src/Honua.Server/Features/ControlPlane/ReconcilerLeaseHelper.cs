// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal static partial class ReconcilerLeaseHelper
{
    internal static readonly TimeSpan LeaseCleanupTimeout = TimeSpan.FromSeconds(5);

    public static async Task RenewLeaseUntilCancelledAsync(
        string leaseId,
        TimeSpan renewInterval,
        CancellationTokenSource reconciliationCancellation,
        Func<CancellationToken, Task<bool>> renewLeaseAsync,
        ILogger logger,
        TimeProvider? clock = null)
    {
        var cancellationToken = reconciliationCancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (clock is null)
                {
                    await Task.Delay(renewInterval, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(renewInterval, clock, cancellationToken).ConfigureAwait(false);
                }

                var renewed = await renewLeaseAsync(cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    CancelBestEffort(reconciliationCancellation);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.LeaseRenewalFailed(logger, leaseId, exception.GetType().Name);
                CancelBestEffort(reconciliationCancellation);
                return;
            }
        }
    }

    public static async Task StopAndReleaseLeaseAsync(
        string leaseId,
        CancellationTokenSource reconciliationCancellation,
        Task renewalTask,
        Func<CancellationToken, Task> releaseLeaseAsync,
        ILogger logger)
    {
        try
        {
            CancelBestEffort(reconciliationCancellation);
            await renewalTask.WaitAsync(LeaseCleanupTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
        {
            // Normal teardown of the renewal loop after cancellation.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.LeaseCleanupFailed(logger, leaseId, exception.GetType().Name);
        }
        finally
        {
            await ReleaseLeaseBestEffortAsync(leaseId, releaseLeaseAsync, logger).ConfigureAwait(false);
        }
    }

    public static async Task ReleaseLeaseBestEffortAsync(
        string leaseId,
        Func<CancellationToken, Task> releaseLeaseAsync,
        ILogger logger)
    {
        using var releasing = new CancellationTokenSource(LeaseCleanupTimeout);
        try
        {
            await releaseLeaseAsync(releasing.Token).WaitAsync(releasing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.LeaseCleanupFailed(logger, leaseId, exception.GetType().Name);
        }
    }

    private static void CancelBestEffort(CancellationTokenSource cts)
    {
        if (!cts.IsCancellationRequested)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently during teardown.
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9092, LogLevel.Warning, "Reconciler lease renewal failed for {LeaseId} ({ExceptionType}); reconciliation is cancelled")]
        public static partial void LeaseRenewalFailed(ILogger logger, string leaseId, string exceptionType);

        [LoggerMessage(9093, LogLevel.Warning, "Reconciler lease cleanup failed for {LeaseId} ({ExceptionType})")]
        public static partial void LeaseCleanupFailed(ILogger logger, string leaseId, string exceptionType);
    }
}
