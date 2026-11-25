// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

internal enum CallerCancellationBehavior
{
    Rethrow,
    Ignore
}

internal sealed class WorkflowLeaseExecutionOptions
{
    public required IWorkflowOperationStore WorkflowStore { get; init; }
    public required string OperationId { get; init; }
    public required string OwnerId { get; init; }
    public required TimeSpan LeaseDuration { get; init; }
    public required TimeSpan LeaseRenewInterval { get; init; }
    public required ILogger Logger { get; init; }
    public required Func<CancellationTokenSource, Task> ExecuteAsync { get; init; }
    public required Action OnLeaseLost { get; init; }
    public CallerCancellationBehavior CallerCancellation { get; init; } = CallerCancellationBehavior.Rethrow;
}

internal static class WorkflowOperationLeaseRunner
{
    public static async Task RunAsync(
        WorkflowLeaseExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.OperationId))
        {
            return;
        }

        var leaseAcquired = await options.WorkflowStore
            .TryAcquireLeaseAsync(options.OperationId, options.OwnerId, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (!leaseAcquired)
        {
            return;
        }

        await ExecuteWithRenewedLeaseAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteWithRenewedLeaseAsync(
        WorkflowLeaseExecutionOptions options,
        CancellationToken cancellationToken)
    {
        using var reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = StartLeaseRenewal(options, reconciliationCancellation);

        try
        {
            await options.ExecuteAsync(reconciliationCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (options.CallerCancellation == CallerCancellationBehavior.Rethrow)
            {
                throw;
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
        {
            options.OnLeaseLost();
        }
        finally
        {
            await ReconcilerLeaseHelper.StopAndReleaseLeaseAsync(new LeaseReleaseRequest
            {
                LeaseId = options.OperationId,
                ReconciliationCancellation = reconciliationCancellation,
                RenewalTask = renewalTask,
                ReleaseLeaseAsync = ct => options.WorkflowStore.ReleaseLeaseAsync(options.OperationId, options.OwnerId, ct),
                Logger = options.Logger
            }).ConfigureAwait(false);
        }
    }

    private static Task StartLeaseRenewal(
        WorkflowLeaseExecutionOptions options,
        CancellationTokenSource reconciliationCancellation)
        => ReconcilerLeaseHelper.RenewLeaseUntilCancelledAsync(new LeaseRenewalRequest
        {
            LeaseId = options.OperationId,
            RenewInterval = options.LeaseRenewInterval,
            ReconciliationCancellation = reconciliationCancellation,
            RenewLeaseAsync = ct => options.WorkflowStore.RenewLeaseAsync(options.OperationId, options.OwnerId, options.LeaseDuration, ct),
            Logger = options.Logger
        });
}
