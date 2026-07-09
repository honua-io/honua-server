// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

/// <summary>
/// Evaluates deterministic ops-finding autonomy policy before the gateway may convert an
/// approval-tier remediation into direct execution.
/// </summary>
public interface IOpsAutonomyEvaluator
{
    /// <summary>
    /// Evaluates whether a finding should be submitted to the operation gateway by the
    /// background autonomy loop.
    /// </summary>
    /// <param name="finding">Finding to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The finding-level decision.</returns>
    Task<OpsAutonomyFindingDecision> EvaluateFindingAsync(
        OpsFinding finding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the route-time policy check that may convert <paramref name="currentDecision"/>
    /// from <see cref="GuardrailTier.RequiresApproval"/> to <see cref="GuardrailTier.DirectExecute"/>.
    /// </summary>
    /// <param name="request">Gateway request.</param>
    /// <param name="currentDecision">Current guardrail decision.</param>
    /// <param name="actionDiscriminator">Resolved action discriminator, when available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The route-time decision.</returns>
    Task<OpsAutonomyRouteDecision> EvaluateRouteAsync(
        OperationGatewayRequest request,
        GuardrailDecision currentDecision,
        string? actionDiscriminator,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the terminal outcome for a route-time auto-apply decision.
    /// </summary>
    /// <param name="decision">The route-time decision that reserved the action.</param>
    /// <param name="outcome">Terminal autonomy outcome.</param>
    /// <param name="operationId">Execution operation identifier, when available.</param>
    /// <param name="message">Optional outcome detail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAutoActionOutcomeAsync(
        OpsAutonomyRouteDecision decision,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a finding-originated request raised a human approval proposal.
    /// </summary>
    /// <param name="request">Gateway request that created a proposal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordProposalRaisedAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);
}
