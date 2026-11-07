// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Microsoft.Extensions.DependencyInjection;
using LegacyExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;
using TypedExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Features.Operations;

internal sealed class LegacyGatewayOperationAdapter : TypedExecutor
{
    private readonly Func<LegacyExecutor> _actuatorFactory;
    private readonly OperationClass _operationClass;

    public LegacyGatewayOperationAdapter(IServiceProvider services, OperationClass operationClass)
        : this(
            () => services.GetServices<LegacyExecutor>()
                .SingleOrDefault(candidate => candidate.OperationClass == operationClass)
                ?? throw new InvalidOperationException(
                    $"No legacy control-plane actuator for operation class '{operationClass}' is composed on this host."),
            operationClass)
    {
    }

    internal LegacyGatewayOperationAdapter(LegacyExecutor actuator)
        : this(() => actuator, actuator.OperationClass)
    {
    }

    private LegacyGatewayOperationAdapter(Func<LegacyExecutor> actuatorFactory, OperationClass operationClass)
    {
        _actuatorFactory = actuatorFactory;
        _operationClass = operationClass;
    }

    private LegacyExecutor Actuator => _actuatorFactory();

    public string OperationId => LegacyOperationIds.For(_operationClass);

    public async Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var gatewayRequest = RequireGatewayRequest(request);
        var plan = gatewayRequest.Plan
            ?? await Actuator.PlanAsync(gatewayRequest, cancellationToken).ConfigureAwait(false);
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
            ApprovedProposalId = context.ApprovedProposalId,
        };
        string? executionId = null;
        string? failureReason = null;
        try
        {
            executionId = await Actuator.ExecuteAsync(gatewayRequest, gatewayRequest.ExecutionPayload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OpsActionException ex) when (_operationClass == OperationClass.AdminConfigChange &&
            ComposeExecutorCodes.IsReportable(ex.Message))
        {
            failureReason = ex.Message;
        }
        var now = DateTimeOffset.UtcNow;
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Canonical operation identity is unavailable."),
            OperationId = OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("Canonical correlation identity is unavailable."),
            Status = (failureReason, executionId, _operationClass) switch
            {
                (not null, _, _) => OperationHandleStatus.Failed,
                (_, not null, OperationClass.Deploy or OperationClass.MetadataRelease or OperationClass.Geoprocess)
                    => OperationHandleStatus.Queued,
                _ => OperationHandleStatus.Completed,
            },
            CreatedAt = now,
            UpdatedAt = now,
            JobId = executionId,
            Reason = failureReason,
            ResourceIds = executionId is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["executionOperationId"] = executionId,
                },
            Result = failureReason is null ? new OperationResultSummary
            {
                Summary = executionId is null
                    ? $"Executed {gatewayRequest.Kind}."
                    : $"Executed {gatewayRequest.Kind} as '{executionId}'.",
            } : null,
        };
    }

    public async Task<OperationStatus> GetStatusAsync(
        OperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var backend = string.IsNullOrWhiteSpace(handle.JobId)
            ? null
            : await Actuator.GetBackendStatusAsync(handle.JobId, cancellationToken).ConfigureAwait(false);
        return new OperationStatus
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
            Status = backend?.State switch
            {
                OperationBackendState.Succeeded => OperationHandleStatus.Completed,
                OperationBackendState.Failed => OperationHandleStatus.Failed,
                OperationBackendState.Cancelled => OperationHandleStatus.Cancelled,
                OperationBackendState.Indeterminate => OperationHandleStatus.Indeterminate,
                OperationBackendState.Running => OperationHandleStatus.Running,
                _ => handle.Status,
            },
            Result = handle.Result,
            JobId = handle.JobId,
            ApprovalLane = handle.ApprovalLane,
            Reason = backend?.Reason ?? handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        };
    }

    private static Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest RequireGatewayRequest(
        OperationRequest request)
        => request.GatewayRequest
            ?? throw new InvalidOperationException("The legacy compatibility request is unavailable.");
}

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
