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

namespace Honua.Core.Features.Operations.Services;

public sealed class CanonicalOperationPolicyDecisionPoint : IOperationPolicyDecisionPoint
{
    private readonly ConfigurableOperationPolicyDecisionPoint _typedPolicy;
    private readonly IGuardrailLadder _legacyGuardrails;
    private readonly Func<CancellationToken, Task<(OperationInitiationKind Kind, OperationInitiationChannel Channel)>>? _resolveInitiation;

    public CanonicalOperationPolicyDecisionPoint(
        CanonicalOperationPolicyConfiguration configuration,
        IGuardrailLadder legacyGuardrails,
        Func<CancellationToken, Task<(OperationInitiationKind Kind, OperationInitiationChannel Channel)>>? resolveInitiation = null)
    {
        _typedPolicy = new ConfigurableOperationPolicyDecisionPoint(configuration.Options, configuration.ProductionEnvironment);
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

        if (AgentApprovalRule.Applies(request.OperationId, context.Roles))
        {
            var agentGuardrail = ResolveGuardrail(gatewayRequest);
            return agentGuardrail.Tier == GuardrailTier.Blocked
                ? new PolicyDecision
                {
                    Kind = PolicyDecisionKind.Deny,
                    Reason = agentGuardrail.Source,
                }
                : AgentApprovalRule.Decision;
        }

        // Autonomous actions have already passed the registered autonomy evaluator.
        // Carry that decision into this single PDP instead of masquerading it as an
        // approved proposal (proposal identity must only identify a real proposal).
        if (string.Equals(context.AuthorizationOutcome, "autonomy-authorized", StringComparison.Ordinal))
        {
            return PolicyDecision.Allowed;
        }

        // Human administrators may publish directly; agents use the approval floor above.
        if (string.Equals(context.AuthorizationOutcome, StudioDraftMutationContext.AdminDirectPublicationOutcome, StringComparison.Ordinal)
            && string.Equals(request.OperationId, StudioDraftMutationContext.PublicationOperationId, StringComparison.Ordinal)
            && !AgentApprovalRule.IsAgent(context.Roles))
        {
            return PolicyDecision.Allowed;
        }

        var guardrail = ResolveGuardrail(gatewayRequest);
        if (IsDefaultPrivateDraftApproval(guardrail, gatewayRequest, descriptor.OperationId)
            && IsOwnedDraftMutation(request, context)
            && await IsInteractiveHumanAsync(cancellationToken).ConfigureAwait(false))
        {
            return PolicyDecision.Allowed;
        }

        return EvaluateGuardrail(guardrail);
    }

    private GuardrailDecision ResolveGuardrail(OperationGatewayRequest request) =>
        request.ActionDiscriminator is null
            ? _legacyGuardrails.Resolve(request.Kind)
            : _legacyGuardrails.Resolve(request.Kind, request.ActionDiscriminator);

    private static PolicyDecision EvaluateGuardrail(GuardrailDecision guardrail) =>
        guardrail.Tier switch
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

    private static bool IsOwnedDraftMutation(OperationRequest request, OperationPolicyContext context) =>
        request.OperationId is not (StudioDraftMutationContext.UpdateOperationId or StudioDraftMutationContext.DeleteOperationId)
        || (!string.IsNullOrWhiteSpace(context.PrincipalId)
            && string.Equals(request.VerifiedStudioDraftOwnerId, context.PrincipalId, StringComparison.Ordinal));

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
