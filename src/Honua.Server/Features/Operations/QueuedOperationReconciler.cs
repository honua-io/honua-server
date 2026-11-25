// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Infrastructure;

namespace Honua.Server.Features.Operations;

// Backend callbacks never own operation-instance transitions.
internal sealed partial class QueuedOperationReconciler(
    IOperationInstanceStore instanceStore,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<QueuedOperationReconciler> logger) : BackgroundService
{
    private const int DefaultMaxItemsPerSweep = 50;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultSweepTimeBudget = TimeSpan.FromSeconds(30);
    private const string LeaseId = "operation-instance-queued-reconciler";
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    internal int MaxItemsPerSweep { get; init; } = DefaultMaxItemsPerSweep;

    internal TimeSpan SweepTimeBudget { get; init; } = DefaultSweepTimeBudget;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => PeriodicBackgroundLoop.RunAsync(
            new PeriodicBackgroundLoopOptions
            {
                Interval = SweepInterval,
                TimeProvider = clock,
                ExecuteIterationAsync = SweepOnceAsync,
                OnIterationError = ex => Log.SweepFailed(logger, ex)
            },
            stoppingToken);

    internal async Task SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await instanceStore.TryAcquireLeaseAsync(LeaseId, _ownerId, LeaseDuration, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        using var sweepBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sweepBudget.CancelAfter(SweepTimeBudget);

        try
        {
            await ReconcileActiveEnvelopesAsync(sweepBudget).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sweepBudget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.SweepBudgetExhausted(logger);
        }
        finally
        {
            await ReconcilerLeaseHelper.ReleaseLeaseBestEffortAsync(
                LeaseId,
                ct => instanceStore.ReleaseLeaseAsync(LeaseId, _ownerId, ct),
                logger).ConfigureAwait(false);
        }
    }

    private async Task ReconcileActiveEnvelopesAsync(CancellationTokenSource sweepBudget)
    {
        var queued = (await instanceStore.ListActiveAsync(sweepBudget.Token).ConfigureAwait(false))
            .Where(envelope => envelope.Status is OperationHandleStatus.Queued or OperationHandleStatus.Running)
            .Take(MaxItemsPerSweep)
            .ToArray();
        foreach (var envelope in queued)
        {
            sweepBudget.Token.ThrowIfCancellationRequested();
            try
            {
                await ReconcileAsync(envelope, sweepBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sweepBudget.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.ItemReconcileFailed(logger, envelope.OperationInstanceId, envelope.OperationId, ex);
            }
        }
    }

    private async Task ReconcileAsync(OperationHandle envelope, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (!string.IsNullOrWhiteSpace(envelope.TenantId))
        {
            // The persisted envelope owns this background scope. Job status reads enforce
            // tenant equality, so restore its accepted tenant before resolving the executor.
            scope.ServiceProvider.GetRequiredService<RequestTenantContext>()
                .Set(envelope.TenantId, TenantContextSource.Default);
        }

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
            MetadataRevision = observed.MetadataRevision,
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

        [LoggerMessage(7429, LogLevel.Warning, "Queued operation reconciliation failed for operation instance '{OperationInstanceId}' ({OperationId}); continuing sweep")]
        public static partial void ItemReconcileFailed(ILogger logger, string operationInstanceId, string operationId, Exception exception);

        [LoggerMessage(7430, LogLevel.Warning, "Queued operation reconciler sweep reached its time budget before lease expiry; remaining items deferred to next sweep")]
        public static partial void SweepBudgetExhausted(ILogger logger);
    }
}
