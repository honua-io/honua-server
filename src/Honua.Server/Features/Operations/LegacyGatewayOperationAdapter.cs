// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.DependencyInjection;
using LegacyExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;
using TypedExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Typed actuator wrapper for one legacy operation-class actuator. It has no policy, identity,
/// proposal, or audit behavior; only the canonical dispatcher may call it. The legacy actuator
/// is resolved at USE time, not construction time: control-plane executors are legitimately
/// registered after <c>AddOperationsToolset</c> (post-Program ConfigureServices), and a
/// registration-time snapshot left the dispatcher without compatibility actuators — converge
/// returned NotSupported/blocked with null operation ids (trunk red 2026-08-29, run
/// 33249627814; the fourth member of the registration-ordering family after #3614/#3617/#3621).
/// Hosts whose final composition lacks the class's actuator get a typed refusal at use time.
/// </summary>
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
        };
        var executionId = await Actuator
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
            Status = executionId is not null && _operationClass is
                OperationClass.Deploy or OperationClass.MetadataRelease or OperationClass.Geoprocess
                    ? OperationHandleStatus.Queued
                    : OperationHandleStatus.Completed,
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
