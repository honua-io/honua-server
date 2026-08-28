// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// The single production policy seam for typed descriptors and legacy compatibility requests.
/// The legacy guardrail ladder is consulted only here and can no longer invoke an actuator.
/// </summary>
public sealed class CanonicalOperationPolicyDecisionPoint : IOperationPolicyDecisionPoint
{
    private readonly ConfigurableOperationPolicyDecisionPoint _typedPolicy;
    private readonly IGuardrailLadder _legacyGuardrails;

    /// <summary>Initializes the canonical policy decision point.</summary>
    public CanonicalOperationPolicyDecisionPoint(
        IOptions<OperationPolicyOptions> options,
        IGuardrailLadder legacyGuardrails)
    {
        _typedPolicy = new ConfigurableOperationPolicyDecisionPoint(options);
        _legacyGuardrails = legacyGuardrails;
    }

    /// <inheritdoc />
    public Task<PolicyDecision> EvaluateAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.GatewayRequest is not { } gatewayRequest)
        {
            return _typedPolicy.EvaluateAsync(descriptor, request, context, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            return Task.FromResult(PolicyDecision.Allowed);
        }

        // Autonomous actions have already passed the registered autonomy evaluator.
        // Carry that decision into this single PDP instead of masquerading it as an
        // approved proposal (proposal identity must only identify a real proposal).
        if (string.Equals(context.AuthorizationOutcome, "autonomy-authorized", StringComparison.Ordinal))
        {
            return Task.FromResult(PolicyDecision.Allowed);
        }

        var guardrail = gatewayRequest.ActionDiscriminator is null
            ? _legacyGuardrails.Resolve(gatewayRequest.Kind)
            : _legacyGuardrails.Resolve(gatewayRequest.Kind, gatewayRequest.ActionDiscriminator);
        return Task.FromResult(guardrail.Tier switch
        {
            GuardrailTier.DirectExecute => PolicyDecision.Allowed,
            GuardrailTier.RequiresApproval => new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "control-plane",
                Reason = guardrail.Source,
            },
            _ => new PolicyDecision
            {
                Kind = PolicyDecisionKind.Deny,
                Reason = guardrail.Source,
            },
        });
    }
}
