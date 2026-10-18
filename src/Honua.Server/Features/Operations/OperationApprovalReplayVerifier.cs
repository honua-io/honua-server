// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Server.Features.Operations;

internal sealed class OperationApprovalReplayVerifier(IOperationProposalStore proposalStore)
    : IOperationApprovalReplayVerifier
{
    public async Task<bool> VerifyAsync(
        string proposalId,
        string operationInstanceId,
        string planHash,
        CancellationToken cancellationToken = default)
    {
        var proposal = await proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        return proposal is
        {
            Status: OperationProposalStatus.Executing,
            SealedPlanHash: not null,
        }
            && string.Equals(
                proposal.Audit.OperationInstanceId,
                operationInstanceId,
                StringComparison.Ordinal)
            && string.Equals(proposal.SealedPlanHash, planHash, StringComparison.Ordinal)
            && string.Equals(
                proposal.SealedPlanHash,
                OperationApprovalPlanSeal.Compute(proposal.Plan),
                StringComparison.Ordinal);
    }
    public async Task<bool> VerifyCommittedReceiptAsync(string proposalId, string operationInstanceId, string planHash,
        CancellationToken cancellationToken = default)
    {
        var proposal = await proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        return proposal is not null && proposal.OperationId == SharedStyleEditOperation.OperationId &&
            proposal.Status is OperationProposalStatus.Executing or OperationProposalStatus.Failed or OperationProposalStatus.Succeeded &&
            !string.IsNullOrWhiteSpace(proposal.ApprovedByAtClaim) && proposal.ApprovedAtClaim is { } approvedAt &&
            approvedAt >= proposal.CreatedAt && approvedAt <= DateTimeOffset.UtcNow &&
            !proposal.ApprovedByAtClaim.Equals(proposal.RequestedBy, StringComparison.OrdinalIgnoreCase) &&
            proposal.Audit.OperationInstanceId == operationInstanceId && proposal.SealedPlanHash == planHash &&
            proposal.SealedPlanHash == OperationApprovalPlanSeal.Compute(proposal.Plan);
    }
}
