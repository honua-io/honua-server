// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Leased startup and periodic janitor that compensates stale non-actionable Planned
/// proposals to Failed with a joined durable audit receipt.
/// </summary>
internal sealed partial class PlannedProposalReconciler(
    IOperationProposalStore proposalStore,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<PlannedProposalReconciler> logger) : BackgroundService
{
    internal static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private const string LeaseId = "operation-proposal-planned-janitor";
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
        if (!await proposalStore
                .TryAcquireLeaseAsync(LeaseId, _ownerId, LeaseDuration, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var cutoff = clock.GetUtcNow() - StaleAge;
            var active = await proposalStore.ListActiveAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var candidate in active.Where(proposal =>
                         proposal.Status == OperationProposalStatus.Planned && proposal.UpdatedAt <= cutoff))
            {
                await CompensateAsync(candidate.ProposalId, cutoff, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await proposalStore.ReleaseLeaseAsync(LeaseId, _ownerId, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task CompensateAsync(
        string proposalId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var proposal = await proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null ||
            proposal.Status != OperationProposalStatus.Planned ||
            proposal.UpdatedAt > cutoff)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        var auditId = await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = clock.GetUtcNow(),
            EventType = AuditEventType.AdminAction,
            Actor = "operation-proposal-janitor",
            ActorType = AuditActorType.System,
            ResourceType = "operation_proposal",
            ResourceId = proposal.ProposalId,
            Action = "operation.proposal.planned-timeout",
            Outcome = AuditOutcome.Failure,
            CorrelationId = proposal.Audit.CorrelationId ?? proposal.ProposalId,
            Details = $"operationInstanceId={proposal.Audit.OperationInstanceId};kind={proposal.Kind}",
        }, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(auditId))
        {
            Log.AuditIdentityMissing(logger, proposal.ProposalId);
            return;
        }

        var now = clock.GetUtcNow();
        var failed = proposal with
        {
            Status = OperationProposalStatus.Failed,
            Audit = proposal.Audit with { AuditId = auditId },
            UpdatedAt = now,
            ResolvedAt = now,
            ResolvedBy = "operation-proposal-janitor",
            ResolutionReason = "Proposal remained Planned beyond the durable acceptance window.",
        };
        if (await proposalStore.TrySetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Log.Compensated(logger, proposal.ProposalId, auditId);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7422, LogLevel.Warning, "Planned proposal janitor sweep failed")]
        public static partial void SweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(7423, LogLevel.Error, "Planned proposal '{ProposalId}' audit did not return an identity")]
        public static partial void AuditIdentityMissing(ILogger logger, string proposalId);

        [LoggerMessage(7424, LogLevel.Warning, "Compensated stale Planned proposal '{ProposalId}' with audit '{AuditId}'")]
        public static partial void Compensated(ILogger logger, string proposalId, string auditId);
    }
}
