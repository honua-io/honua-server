// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;

namespace Honua.Core.Features.Operations.Services;

// Per-agent authority ceilings: checked with policy, and again immediately before actuation.
public sealed partial class OperationDispatcher
{
    private async Task<PolicyDecision> EvaluateSubmissionDecisionAsync(
        OperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext invocationContext,
        CancellationToken cancellationToken)
    {
        var verdict = await AgentAuthorityPolicy.EvaluateAsync(_agentAuthorityStore, descriptor, invocationContext, cancellationToken)
            .ConfigureAwait(false);
        if (verdict == AgentAuthorityVerdict.Denied)
        {
            return new PolicyDecision { Kind = PolicyDecisionKind.Deny, Reason = "Operation exceeds the agent authority ceiling." };
        }

        if (!string.IsNullOrWhiteSpace(invocationContext.ApprovedProposalId))
        {
            return PolicyDecision.Allowed;
        }

        var decision = await _policy.EvaluateAsync(descriptor, request, invocationContext, cancellationToken).ConfigureAwait(false);
        return verdict == AgentAuthorityVerdict.ApprovalRequired && !request.DryRun && decision.Kind == PolicyDecisionKind.Allow
            ? AgentApprovalRule.Decision
            : decision;
    }

    // Only trusted proposal submission adds an approval floor. A refusal is never promoted,
    // and verified approved replay remains on the ordinary submission path.
    private static PolicyDecision RequireProposalApproval(PolicyDecision decision)
        => decision.Kind == PolicyDecisionKind.Allow
            ? new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "control-plane",
                Reason = "Operator-gated proposal submission requires separate approval.",
            }
            : decision;

    // Re-reads the ceiling just before actuation. Returns the persisted denial when authority was
    // revoked after policy evaluation (or now needs a human approval this call does not carry), or
    // null when actuation may start. Cancellation here happens
    // before any side effect and is recorded as such.
    private async Task<OperationHandle?> RefuseRevokedAgentAuthorityAsync(
        OperationHandle envelope,
        OperationDescriptor descriptor,
        OperationPolicyContext invocationContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var verdict = await AgentAuthorityPolicy.EvaluateAsync(_agentAuthorityStore, descriptor, invocationContext, cancellationToken)
                .ConfigureAwait(false);
            if (verdict == AgentAuthorityVerdict.Unrestricted
                || (verdict == AgentAuthorityVerdict.ApprovalRequired && !string.IsNullOrWhiteSpace(invocationContext.ApprovedProposalId)))
            {
                return null;
            }

            return await PersistAsync(
                    envelope with
                    {
                        Status = OperationHandleStatus.Denied,
                        PolicyDecision = PolicyDecisionKind.Deny,
                        UpdatedAt = _clock.GetUtcNow(),
                        Reason = "Agent authority was revoked before actuation.",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PersistPreActuationCancellationAsync(envelope, invocationContext).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"Operation actuation failed ({ex.GetType().Name}).",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
