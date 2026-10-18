// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Styling;

namespace Honua.ControlPlane;

internal sealed partial class OperationGateway
{
    public async Task<OperationProposal?> ReconcileCommittedReceiptAsync(string proposalId, ClaimsPrincipal requester,
        CancellationToken cancellationToken = default)
    {
        var proposal = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.OperationId != SharedStyleEditOperation.OperationId ||
            proposal.Status is not (OperationProposalStatus.Executing or OperationProposalStatus.Failed) ||
            string.IsNullOrWhiteSpace(proposal.ApprovedByAtClaim) || proposal.ApprovedAtClaim is null ||
            proposal.ApprovedAtClaim < proposal.CreatedAt || proposal.ApprovedAtClaim > DateTimeOffset.UtcNow ||
            proposal.ApprovedByAtClaim.Equals(proposal.RequestedBy, StringComparison.OrdinalIgnoreCase) ||
            CanonicalSecurityActor.Resolve(requester)?.ActorId != proposal.RequestedBy ||
            !requester.HasClaim("permission", "agent:workspace") ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) return null;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var mapper = services.GetServices<IOperationApprovalRequestMapper>()
            .SingleOrDefault(candidate => candidate.OperationId == SharedStyleEditOperation.OperationId);
        var replay = mapper?.MapReplay(RebuildRequest(proposal));
        if (replay is null) return null;
        var tenant = services.GetService<RequestTenantContext>();
        tenant?.Set(replay.TenantId, Honua.Core.Features.MultiTenancy.Abstractions.TenantContextSource.Claim);
        if (services.GetService<SchemaContext>() is { } schema) schema.CurrentSchema = replay.SchemaName;
        var context = new OperationPolicyContext
        {
            OperationInstanceId = proposal.Audit.OperationInstanceId,
            CorrelationId = proposal.Audit.CorrelationId,
            PrincipalId = proposal.RequestedBy,
            TenantId = replay.TenantId,
            SchemaName = replay.SchemaName,
            ApprovedProposalId = proposal.ProposalId,
            ApprovedPlanHash = proposal.SealedPlanHash,
            ScopeGoverned = proposal.ScopeGoverned,
            RecognizedScopes = proposal.RecognizedScopes,
            AuthorizationOutcome = "approved-receipt-reconciliation",
        };
        // The exact SQL proof and current key, role, workspace and readable scope are verified
        // BEFORE entering the dispatcher. A missing receipt cannot create any canonical state.
        var verified = await SharedStyleApprovedReplay.VerifyAsync(services, replay.Request, context, cancellationToken, receiptOnly: true).ConfigureAwait(false);
        if (verified.Receipt is null) return null;
        await verified.RevalidateReceiptAsync(cancellationToken).ConfigureAwait(false);
        var handle = await services.GetRequiredService<IOperationInvoker>()
            .ReconcileCommittedReceiptAsync(replay.Request, context, cancellationToken).ConfigureAwait(false);
        if (handle?.Status != OperationHandleStatus.Completed)
        {
            var winner = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
            return winner?.Status == OperationProposalStatus.Succeeded && winner.SealedPlanHash == proposal.SealedPlanHash &&
                winner.ApprovedByAtClaim == proposal.ApprovedByAtClaim ? winner : null;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var latest = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
            if (latest is null) return null;
            if (latest.Status is not (OperationProposalStatus.Executing or OperationProposalStatus.Failed)) return latest;
            if (latest.ApprovedByAtClaim != proposal.ApprovedByAtClaim || latest.ApprovedAtClaim != proposal.ApprovedAtClaim ||
                latest.SealedPlanHash != proposal.SealedPlanHash) return null;
            var resolved = latest with
            {
                Status = OperationProposalStatus.Succeeded,
                ResolutionReason = null,
                ResolvedBy = proposal.ApprovedByAtClaim,
                ResolvedAt = DateTimeOffset.UtcNow
            };
            if (await _proposalStore.TrySetAsync(resolved, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                // Same terminal publication semantics as normal approval completion, preserving
                // the durably claimed human approver rather than attributing approval to a poller.
                await RecordAutonomyProposalResolutionAsync(resolved, OpsAutonomyProposalResolution.Approved, cancellationToken).ConfigureAwait(false);
                await WriteAuditAsync(resolved, "operation.receipt_reconciled", proposal.ApprovedByAtClaim,
                    AuditOutcome.Success, cancellationToken).ConfigureAwait(false);
                await _notifier.NotifyResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
                return await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("The proposal receipt raced another transition; retry status.");
    }
}
