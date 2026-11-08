// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Features.Operations;

namespace Honua.ControlPlane;

internal static class OperationHumanApprovalEvidenceValidation
{
    public static bool MatchesExecutionRequest(OperationProposal proposal, OperationGatewayRequest request, string payload) =>
        proposal.Kind == OperationClass.Deploy
        && HasExecutionAuthority(proposal)
        && proposal.ProposalId == request.ApprovedProposalId
        && proposal.RequestedBy == request.RequestedBy
        && proposal.ResolvedBy == request.ApprovedBy
        && proposal.TenantId == request.TenantId
        && proposal.Audit.OperationInstanceId == request.OperationInstanceId
        && proposal.Plan.ExecutionPayload == payload;

    public static bool MatchesObservedForwardDeploy(string approvedPayload, DeployExecutionPayload observed)
    {
        var approved = DeployExecutionPayload.Parse(approvedPayload);
        return approved is { Rollback: false }
            && (observed with { CurrentRevision = approved.CurrentRevision }).Serialize() == approved.Serialize();
    }

    public static bool HasExecutionAuthority(OperationProposal proposal)
    {
        if (proposal.Status is not (OperationProposalStatus.Executing or OperationProposalStatus.Submitted
            or OperationProposalStatus.Reconciling or OperationProposalStatus.Succeeded)) return false;
        if (proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) return false;

        // An existing legacy execution claim keeps its recorded authority; pending legacy proposals cannot claim through the gateway.
        if (proposal.Initiation is null && proposal.HumanApproval is null)
            return !string.IsNullOrWhiteSpace(proposal.ResolvedBy);

        return proposal is
        {
            Initiation: { Version: OperationInitiationEvidence.CurrentVersion } initiation,
            HumanApproval: { Version: OperationHumanApprovalEvidence.CurrentVersion } approval,
            GuardrailDecision: { } policy,
        }
            && Enum.IsDefined(initiation.Kind) && Enum.IsDefined(initiation.Channel)
            && initiation.PrincipalId == proposal.RequestedBy && initiation.TenantId == proposal.TenantId
            && initiation.PlanHash == proposal.SealedPlanHash
            && initiation.PolicyRevision == OperationGateway.ComputePolicyRevision(policy)
            && approval.Channel == OperationHumanApprovalEvidence.InteractiveOidcChannel
            && !string.IsNullOrWhiteSpace(approval.ActorId) && approval.ActorId == proposal.ResolvedBy
            && approval.PlanHash == proposal.SealedPlanHash
            && approval.ProposalVersion >= 0 && approval.ProposalVersion < proposal.Version
            && IsReference(approval.SessionReference) && IsReference(approval.CapabilityReference)
            && approval.InitiationDigest == OperationHumanApprovalAuthority.ComputeInitiationDigest(initiation)
            && approval.BindingDigest == OperationHumanApprovalAuthority.ComputeBindingDigest(proposal);
    }

    private static bool IsReference(string reference) => reference is { Length: 64 } && reference.All(char.IsAsciiHexDigit);
}
