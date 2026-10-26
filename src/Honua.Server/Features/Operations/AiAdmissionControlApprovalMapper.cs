// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal sealed class AiAdmissionControlApprovalMapper(string operationId) : IOperationApprovalRequestMapper
{
    public string OperationId => operationId;

    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request, OperationPolicyContext context, PolicyDecision decision)
    {
        if (request.OperationId != OperationId || descriptor.OperationId != OperationId
            || !request.Parameters.TryGetValue(AiAdmissionControlOperation.ActorPin, out var actor)
            || string.IsNullOrWhiteSpace(actor) || actor != context.PrincipalId)
            throw new InvalidOperationException("AI admission approval requires a server-prepared requester.");
        var payload = JsonSerializer.Serialize(AdminApiOperationApprovalPayload.From(request, context),
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationId = OperationId, OperationInstanceId = context.OperationInstanceId, Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId, Reason = decision.Reason, CorrelationId = context.CorrelationId,
            IdempotencyKey = context.IdempotencyKey, ExecutionPayload = payload,
            Plan = new OperationProposalPlan { Summary = "Apply " + OperationId + " to the accepted deployment and policy epoch.",
                RiskLevel = ProposalRiskLevel.High, ExecutionPayload = payload }
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        if (request.OperationId != OperationId || request.Kind != OperationClass.AdminConfigChange)
            throw new InvalidOperationException("AI admission approval identity mismatch.");
        var payload = JsonSerializer.Deserialize(request.Plan?.ExecutionPayload ?? request.ExecutionPayload
            ?? throw new InvalidOperationException("AI admission approval payload is missing."),
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
            ?? throw new InvalidOperationException("AI admission approval payload is invalid.");
        if (payload.OperationId != OperationId || !payload.Parameters.TryGetValue(AiAdmissionControlOperation.ActorPin, out var actor)
            || string.IsNullOrWhiteSpace(actor) || actor != request.RequestedBy)
            throw new InvalidOperationException("AI admission sealed requester mismatch.");
        return new OperationApprovalReplayMapping
        {
            Request = payload.ToOperationRequest(), TenantId = payload.TenantId, SchemaName = payload.SchemaName
        };
    }
}
