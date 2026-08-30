// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Leased authority that polls durable backend jobs and advances canonical queued envelopes.
/// Backend callbacks never own operation-instance transitions.
/// </summary>
internal sealed partial class QueuedOperationReconciler(
    IOperationInstanceStore instanceStore,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<QueuedOperationReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private const string LeaseId = "operation-instance-queued-reconciler";
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.SweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(SweepInterval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await instanceStore.TryAcquireLeaseAsync(LeaseId, _ownerId, LeaseDuration, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var queued = (await instanceStore.ListActiveAsync(cancellationToken).ConfigureAwait(false))
                .Where(envelope => envelope.Status is OperationHandleStatus.Queued or OperationHandleStatus.Running)
                .ToArray();
            foreach (var envelope in queued)
            {
                await ReconcileAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await instanceStore.ReleaseLeaseAsync(LeaseId, _ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(OperationHandle envelope, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetServices<IOperationExecutor>()
            .SingleOrDefault(candidate => string.Equals(candidate.OperationId, envelope.OperationId, StringComparison.Ordinal));
        if (executor is null)
        {
            Log.ExecutorMissing(logger, envelope.OperationInstanceId, envelope.OperationId);
            return;
        }

        var observed = await executor.GetStatusAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (observed.Status == envelope.Status)
        {
            return;
        }

        var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        var auditId = await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = clock.GetUtcNow(),
            EventType = AuditEventType.AdminAction,
            Actor = "operation-instance-reconciler",
            ActorType = AuditActorType.System,
            ResourceType = "operation_instance",
            ResourceId = envelope.OperationInstanceId,
            Action = AuditAction(observed.Status),
            Outcome = observed.Status is OperationHandleStatus.Completed or OperationHandleStatus.Running
                ? AuditOutcome.Success
                : AuditOutcome.Failure,
            CorrelationId = envelope.CorrelationId,
            Details = $"operationId={envelope.OperationId};jobId={envelope.JobId};status={observed.Status}",
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(auditId))
        {
            Log.AuditMissing(logger, envelope.OperationInstanceId);
            return;
        }

        var updated = envelope with
        {
            Status = observed.Status,
            AuditId = auditId,
            UpdatedAt = clock.GetUtcNow(),
            Result = observed.Result,
            Reason = observed.Reason,
            ResourceIds = observed.ResourceIds,
            EvidenceRefs = [.. observed.EvidenceRefs, $"backend-transition-audit:{auditId}"],
        };
        if (!await instanceStore.TrySetAsync(updated, envelope.Version, cancellationToken).ConfigureAwait(false))
        {
            Log.VersionConflict(logger, envelope.OperationInstanceId, envelope.Version);
        }
    }

    private static string AuditAction(OperationHandleStatus status) => status switch
    {
        OperationHandleStatus.Completed => "operation.completed",
        OperationHandleStatus.Running => "operation.running",
        OperationHandleStatus.Cancelled => "operation.cancelled",
        OperationHandleStatus.Indeterminate => "operation.indeterminate",
        _ => "operation.failed",
    };

    private static partial class Log
    {
        [LoggerMessage(7425, LogLevel.Warning, "Queued operation reconciler sweep failed")]
        public static partial void SweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(7426, LogLevel.Error, "No actuator can reconcile operation instance '{OperationInstanceId}' ({OperationId})")]
        public static partial void ExecutorMissing(ILogger logger, string operationInstanceId, string operationId);

        [LoggerMessage(7427, LogLevel.Error, "Reconciliation audit identity was unavailable for operation instance '{OperationInstanceId}'")]
        public static partial void AuditMissing(ILogger logger, string operationInstanceId);

        [LoggerMessage(7428, LogLevel.Warning, "Refused stale reconciliation write for operation instance '{OperationInstanceId}' at version {ExpectedVersion}")]
        public static partial void VersionConflict(ILogger logger, string operationInstanceId, long expectedVersion);
    }
}
