// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Seals the rollback target and accepted safety classifications for durable replay.
/// </summary>
internal sealed class WorkflowRollbackApprovalRequestMapper(string operationId) : IOperationApprovalRequestMapper
{
    public string OperationId { get; } = operationId is WorkflowRollbackOperations.Deploy
        or WorkflowRollbackOperations.CoordinatedRelease
            ? operationId
            : throw new ArgumentException("Unsupported rollback operation.", nameof(operationId));

    public OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision)
    {
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId)
        {
            throw new ArgumentException("Rollback approval requires the exact operation identity.", nameof(request));
        }

        Validate(request);
        var payload = JsonSerializer.Serialize(AdminApiOperationApprovalPayload.From(request, context),
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationId = OperationId,
            OperationInstanceId = context.OperationInstanceId,
            Kind = OperationClass.Deploy,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            IdempotencyKey = context.IdempotencyKey,
            ExecutionPayload = payload,
            Plan = new OperationProposalPlan
            {
                Summary = $"Execute {OperationId} against the accepted workflow target.",
                RiskLevel = ProposalRiskLevel.High,
                ExecutionPayload = payload,
            },
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        if (request.OperationId != OperationId || request.Kind != OperationClass.Deploy)
        {
            throw new InvalidOperationException("Rollback replay identity does not match its mapper.");
        }

        var payload = JsonSerializer.Deserialize(
            request.Plan?.ExecutionPayload ?? request.ExecutionPayload
                ?? throw new InvalidOperationException("The sealed rollback payload is unavailable."),
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
            ?? throw new InvalidOperationException("The sealed rollback payload is invalid.");
        if (payload.OperationId != OperationId)
        {
            throw new InvalidOperationException("The sealed rollback identity does not match its mapper.");
        }

        var replay = payload.ToOperationRequest();
        Validate(replay);
        return new OperationApprovalReplayMapping
        {
            Request = replay,
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }

    private void Validate(OperationRequest request)
    {
        if (!request.Parameters.TryGetValue(WorkflowRollbackOperations.TargetOperationId, out var target)
            || string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Rollback requires a workflow target.", nameof(request));
        }

        if (OperationId == WorkflowRollbackOperations.Deploy
            && (!request.Parameters.TryGetValue(WorkflowRollbackOperations.ApprovedDataAffecting, out var dataAffecting)
                || !bool.TryParse(dataAffecting, out _)
                || !request.Parameters.TryGetValue(WorkflowRollbackOperations.ApprovedRequiresApproval, out var requiresApproval)
                || !bool.TryParse(requiresApproval, out _)))
        {
            throw new ArgumentException("Deploy rollback requires both accepted safety classifications.", nameof(request));
        }
    }
}
