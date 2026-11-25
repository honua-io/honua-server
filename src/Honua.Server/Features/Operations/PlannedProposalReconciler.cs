// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure;

namespace Honua.Server.Features.Operations;

internal sealed partial class PlannedProposalReconciler(
    IOperationProposalStore proposalStore,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<PlannedProposalReconciler> logger) : BackgroundService
{
    internal static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultLeaseRenewInterval = TimeSpan.FromSeconds(15);
    private const string LeaseId = "operation-proposal-planned-janitor";
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    internal TimeSpan LeaseDuration { get; init; } = DefaultLeaseDuration;

    internal TimeSpan LeaseRenewInterval { get; init; } = DefaultLeaseRenewInterval;

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
        if (!await proposalStore
                .TryAcquireLeaseAsync(LeaseId, _ownerId, LeaseDuration, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        using var sweepCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = ReconcilerLeaseHelper.RenewLeaseUntilCancelledAsync(new LeaseRenewalRequest
        {
            LeaseId = LeaseId,
            RenewInterval = LeaseRenewInterval,
            ReconciliationCancellation = sweepCancellation,
            RenewLeaseAsync = ct => proposalStore.RenewLeaseAsync(LeaseId, _ownerId, LeaseDuration, ct),
            Logger = logger,
            Clock = clock
        });

        try
        {
            await CompensateStaleProposalsAsync(sweepCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sweepCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.SweepLeaseLost(logger);
        }
        finally
        {
            await ReconcilerLeaseHelper.StopAndReleaseLeaseAsync(new LeaseReleaseRequest
            {
                LeaseId = LeaseId,
                ReconciliationCancellation = sweepCancellation,
                RenewalTask = renewalTask,
                ReleaseLeaseAsync = ct => proposalStore.ReleaseLeaseAsync(LeaseId, _ownerId, ct),
                Logger = logger
            }).ConfigureAwait(false);
        }
    }

    private async Task CompensateStaleProposalsAsync(CancellationTokenSource sweepCancellation)
    {
        var cutoff = clock.GetUtcNow() - StaleAge;
        var active = await proposalStore.ListActiveAsync(cancellationToken: sweepCancellation.Token)
            .ConfigureAwait(false);
        foreach (var candidate in active.Where(proposal =>
                     proposal.Status == OperationProposalStatus.Planned && proposal.UpdatedAt <= cutoff))
        {
            sweepCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                await CompensateAsync(candidate.ProposalId, cutoff, sweepCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sweepCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.ProposalCompensationFailed(logger, candidate.ProposalId, ex);
            }
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

        [LoggerMessage(7431, LogLevel.Warning, "Planned proposal compensation failed for '{ProposalId}'; continuing sweep")]
        public static partial void ProposalCompensationFailed(ILogger logger, string proposalId, Exception exception);

        [LoggerMessage(7432, LogLevel.Warning, "Planned proposal janitor lost its lease during sweep; remaining proposals deferred")]
        public static partial void SweepLeaseLost(ILogger logger);
    }
}
