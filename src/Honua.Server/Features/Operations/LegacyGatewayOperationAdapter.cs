// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using LegacyExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;
using TypedExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Typed actuator wrapper for one legacy operation-class actuator. It has no policy, identity,
/// proposal, or audit behavior; only the canonical dispatcher may call it.
/// </summary>
internal sealed class LegacyGatewayOperationAdapter(LegacyExecutor actuator) : TypedExecutor
{
    public string OperationId => LegacyOperationIds.For(actuator.OperationClass);

    public async Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var gatewayRequest = RequireGatewayRequest(request);
        var plan = gatewayRequest.Plan
            ?? await actuator.PlanAsync(gatewayRequest, cancellationToken).ConfigureAwait(false);
        return new OperationValidation
        {
            IsValid = true,
            Status = "valid",
            ApprovalPlan = plan,
        };
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var gatewayRequest = RequireGatewayRequest(request) with
        {
            OperationInstanceId = context.OperationInstanceId,
            CorrelationId = context.CorrelationId,
        };
        var executionId = await actuator
            .ExecuteAsync(gatewayRequest, gatewayRequest.ExecutionPayload, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Canonical operation identity is unavailable."),
            OperationId = OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("Canonical correlation identity is unavailable."),
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            JobId = executionId,
            ResourceIds = executionId is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["executionOperationId"] = executionId,
                },
            Result = new OperationResultSummary
            {
                Summary = executionId is null
                    ? $"Executed {gatewayRequest.Kind}."
                    : $"Executed {gatewayRequest.Kind} as '{executionId}'.",
            },
        };
    }

    public Task<OperationStatus> GetStatusAsync(
        OperationHandle handle,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            AuditId = handle.AuditId,
            ProposalId = handle.ProposalId,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            AuthorizationOutcome = handle.AuthorizationOutcome,
            PolicyDecision = handle.PolicyDecision,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            ApprovalLane = handle.ApprovalLane,
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        });

    private static Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest RequireGatewayRequest(
        OperationRequest request)
        => request.GatewayRequest
            ?? throw new InvalidOperationException("The legacy compatibility request is unavailable.");
}

/// <summary>Canonical descriptor ids for legacy operation-class compatibility adapters.</summary>
internal static class LegacyOperationIds
{
    public static string For(OperationClass operationClass) => operationClass switch
    {
        OperationClass.Deploy => "control-plane.deploy",
        OperationClass.Seed => "control-plane.seed",
        OperationClass.AdminConfigChange => "control-plane.admin-config-change",
        OperationClass.MetadataRelease => "control-plane.metadata-release",
        OperationClass.Geoprocess => "control-plane.geoprocess",
        OperationClass.ServicePublish => ServicePublishOperation.OperationId,
        _ => throw new ArgumentOutOfRangeException(nameof(operationClass), operationClass, null),
    };
}
