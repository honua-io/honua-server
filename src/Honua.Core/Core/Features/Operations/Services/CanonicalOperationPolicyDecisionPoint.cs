// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Guardrails;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Operations.Services;

public sealed class CanonicalOperationPolicyDecisionPoint : IOperationPolicyDecisionPoint
{
    private readonly ConfigurableOperationPolicyDecisionPoint _typedPolicy;
    private readonly IGuardrailLadder _legacyGuardrails;
    private readonly Func<CancellationToken, Task<(OperationInitiationKind Kind, OperationInitiationChannel Channel)>>? _resolveInitiation;

    public CanonicalOperationPolicyDecisionPoint(
        IOptions<OperationPolicyOptions> options,
        IGuardrailLadder legacyGuardrails,
        bool productionEnvironment = false,
        Func<CancellationToken, Task<(OperationInitiationKind Kind, OperationInitiationChannel Channel)>>? resolveInitiation = null)
    {
        _typedPolicy = new ConfigurableOperationPolicyDecisionPoint(options, productionEnvironment);
        _legacyGuardrails = legacyGuardrails;
        _resolveInitiation = resolveInitiation;
    }

    public OperationPolicyComposition TypedPolicy => _typedPolicy.Composition;

    public async Task<PolicyDecision> EvaluateAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.GatewayRequest is not { } gatewayRequest)
        {
            return await _typedPolicy.EvaluateAsync(descriptor, request, context, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            return PolicyDecision.Allowed;
        }

        // Autonomous actions have already passed the registered autonomy evaluator.
        // Carry that decision into this single PDP instead of masquerading it as an
        // approved proposal (proposal identity must only identify a real proposal).
        if (string.Equals(context.AuthorizationOutcome, "autonomy-authorized", StringComparison.Ordinal))
        {
            return PolicyDecision.Allowed;
        }

        // An admin Studio publication executes before the proposal guardrail. The
        // outcome is scoped to that one operation so it cannot loosen any other class.
        // It is not an approved proposal: the same principal must not be sent through
        // the approve route (honua-server#5207).
        if (string.Equals(context.AuthorizationOutcome, StudioDraftMutationContext.AdminDirectPublicationOutcome, StringComparison.Ordinal)
            && string.Equals(request.OperationId, StudioDraftMutationContext.PublicationOperationId, StringComparison.Ordinal))
        {
            return PolicyDecision.Allowed;
        }

        var guardrail = gatewayRequest.ActionDiscriminator is null
            ? _legacyGuardrails.Resolve(gatewayRequest.Kind)
            : _legacyGuardrails.Resolve(gatewayRequest.Kind, gatewayRequest.ActionDiscriminator);
        if (IsDefaultPrivateDraftApproval(guardrail, gatewayRequest, descriptor.OperationId)
            && await IsInteractiveHumanAsync(cancellationToken).ConfigureAwait(false))
        {
            return PolicyDecision.Allowed;
        }

        return guardrail.Tier switch
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
        };
    }

    private async Task<bool> IsInteractiveHumanAsync(CancellationToken cancellationToken)
    {
        if (_resolveInitiation is null) return false;
        var initiation = await _resolveInitiation(cancellationToken).ConfigureAwait(false);
        return initiation is (OperationInitiationKind.Human, OperationInitiationChannel.InteractiveOidc);
    }

    private static bool IsDefaultPrivateDraftApproval(
        GuardrailDecision guardrail, OperationGatewayRequest request, string operationId) =>
        guardrail is { Tier: GuardrailTier.RequiresApproval, Source: DefaultGuardrailLadder.DefaultPolicySource }
        && request is { Kind: OperationClass.StudioDraftMutation, ActionDiscriminator: null }
        && StudioDraftMutationContext.IsPrivateDraftOperation(operationId);
}
