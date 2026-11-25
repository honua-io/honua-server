// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal sealed class LeaseRenewalRequest
{
    public required string LeaseId { get; init; }
    public required TimeSpan RenewInterval { get; init; }
    public required CancellationTokenSource ReconciliationCancellation { get; init; }
    public required Func<CancellationToken, Task<bool>> RenewLeaseAsync { get; init; }
    public required ILogger Logger { get; init; }
    public TimeProvider? Clock { get; init; }
}

internal sealed class LeaseReleaseRequest
{
    public required string LeaseId { get; init; }
    public required CancellationTokenSource ReconciliationCancellation { get; init; }
    public required Task RenewalTask { get; init; }
    public required Func<CancellationToken, Task> ReleaseLeaseAsync { get; init; }
    public required ILogger Logger { get; init; }
}

internal static partial class ReconcilerLeaseHelper
{
    internal static readonly TimeSpan LeaseCleanupTimeout = TimeSpan.FromSeconds(5);

    public static async Task RenewLeaseUntilCancelledAsync(LeaseRenewalRequest request)
    {
        var cancellationToken = request.ReconciliationCancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await DelayForRenewalAsync(request.RenewInterval, request.Clock, cancellationToken).ConfigureAwait(false);
                var renewed = await request.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    CancelBestEffort(request.ReconciliationCancellation);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.LeaseRenewalFailed(request.Logger, request.LeaseId, exception.GetType().Name);
                CancelBestEffort(request.ReconciliationCancellation);
                return;
            }
        }
    }

    public static async Task StopAndReleaseLeaseAsync(LeaseReleaseRequest request)
    {
        try
        {
            CancelBestEffort(request.ReconciliationCancellation);
            await request.RenewalTask.WaitAsync(LeaseCleanupTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.ReconciliationCancellation.IsCancellationRequested)
        {
            // Normal teardown of the renewal loop after cancellation.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.LeaseCleanupFailed(request.Logger, request.LeaseId, exception.GetType().Name);
        }
        finally
        {
            await ReleaseLeaseBestEffortAsync(request.LeaseId, request.ReleaseLeaseAsync, request.Logger).ConfigureAwait(false);
        }
    }

    private static Task DelayForRenewalAsync(TimeSpan renewInterval, TimeProvider? clock, CancellationToken cancellationToken)
        => clock is null
            ? Task.Delay(renewInterval, cancellationToken)
            : Task.Delay(renewInterval, clock, cancellationToken);

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
