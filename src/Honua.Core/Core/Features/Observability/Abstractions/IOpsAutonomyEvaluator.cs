// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IOpsAutonomyEvaluator
{
    Task<OpsAutonomyFindingDecision> EvaluateFindingAsync(
        OpsFinding finding,
        CancellationToken cancellationToken = default);

    Task<OpsAutonomyRouteDecision> EvaluateRouteAsync(
        OperationGatewayRequest request,
        GuardrailDecision currentDecision,
        string? actionDiscriminator,
        CancellationToken cancellationToken = default);

    Task RecordAutoActionOutcomeAsync(
        OpsAutonomyRouteDecision decision,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default);

    Task RecordProposalRaisedAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task RecordProposalResolutionAsync(
        OperationProposal proposal,
        OpsAutonomyProposalResolution resolution,
        CancellationToken cancellationToken = default);
}
