// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Operations;

internal static class WorkflowRollbackOperations
{
    public const string Deploy = "control-plane.deploy.rollback";
    public const string CoordinatedRelease = "control-plane.coordinated-release.rollback";
    public const string TargetOperationId = "targetOperationId";
    public const string Reason = "reason";
    public const string ApprovedDataAffecting = "approvedDataAffecting";
    public const string ApprovedRequiresApproval = "approvedRequiresApproval";

    public static IReadOnlyList<IOperationDescriptor> BuildDescriptors() =>
    [
        Build(Deploy, "Roll back deploy workflow"),
        Build(CoordinatedRelease, "Roll back coordinated release workflow"),
    ];

    private static OperationDescriptor Build(string operationId, string title) => new()
    {
        OperationId = operationId,
        ProviderId = ServicePublishOperation.ProviderId,
        Title = title,
        Description = "Requests workflow rollback through the canonical durable operation runtime.",
        Category = "control-plane",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.DeploymentScope,
            SideEffectClass = OperationSideEffectClass.DestroysState,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = false,
        },
        InputSchema = BuildInputSchema(operationId),
        OutputSchema = [],
    };

    private static List<OperationParameterDescriptor> BuildInputSchema(string operationId)
    {
        var parameters = new List<OperationParameterDescriptor>
        {
            new()
            {
                Name = TargetOperationId,
                Title = "Target workflow operation id",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
            },
        };
        if (string.Equals(operationId, Deploy, StringComparison.Ordinal))
        {
            parameters.Add(new OperationParameterDescriptor
            {
                Name = ApprovedDataAffecting,
                Title = "Approved rollback data-affecting classification",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Flag },
            });
            parameters.Add(new OperationParameterDescriptor
            {
                Name = ApprovedRequiresApproval,
                Title = "Approved rollback explicit-approval classification",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Flag },
            });
        }

        return parameters;
    }

    public static string? ScopeIdempotencyKey(string? idempotencyKey, string targetOperationId)
        => string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : $"{targetOperationId.Length}:{targetOperationId}:{idempotencyKey}";

    public static bool IsNotFound(OperationHandle handle)
        => handle.Status == OperationHandleStatus.Failed
            && handle.Result?.Details.TryGetValue("errorKind", out var errorKind) == true
            && string.Equals(errorKind, "not-found", StringComparison.Ordinal);

    public static bool IsConflict(OperationHandle handle)
        => handle.Status == OperationHandleStatus.Failed
            && handle.Result?.Details.TryGetValue("errorKind", out var errorKind) == true
            && string.Equals(errorKind, "conflict", StringComparison.Ordinal);
}

internal abstract class WorkflowRollbackOperationExecutor(IServiceProvider services, TimeProvider clock) : IOperationExecutor
{
    protected IServiceProvider Services { get; } = services;
    protected TimeProvider Clock { get; } = clock;
    public abstract string OperationId { get; }

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        _ = Required(request, WorkflowRollbackOperations.TargetOperationId);
        ValidateAdditionalParameters(request);
        return Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var targetId = Required(request, WorkflowRollbackOperations.TargetOperationId);

        // Deploy targets and coordinated releases are platform resources. Every submission
        // surface (the REST wrappers, /api/v1/operations/{id}/submit, MCP and approval replay)
        // reaches this executor, so the platform authority boundary is enforced here rather
        // than only in the hand-authored REST handlers (#5625).
        if (Services.GetService<IHttpContextAccessor>()?.HttpContext?.User is { } principal &&
            !PlatformDeployAuthority.IsAuthorized(principal, Services.GetService<IOptions<TenantContextOptions>>()?.Value))
        {
            return CreatePlatformAuthorityDeniedHandle(context, targetId);
        }

        WorkflowOperationRecord? result = null;
        string? errorKind = null;
        string? failureReason = null;
        try
        {
            result = await RollbackAsync(request, context, targetId, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceConflictException ex)
        {
            errorKind = "conflict";
            failureReason = ex.Message;
        }

        var now = Clock.GetUtcNow();
        var failed = result is null;
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Workflow rollback requires a canonical operation instance."),
            OperationId = OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("Workflow rollback requires a canonical correlation identity."),
            Status = failed ? OperationHandleStatus.Failed : OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = failureReason ?? (failed ? $"Workflow operation '{targetId}' was not found." : null),
            ResourceIds = failed
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workflowOperationId"] = result!.OperationId,
                },
            Result = new OperationResultSummary
            {
                Summary = failed
                    ? failureReason ?? $"Workflow operation '{targetId}' was not found."
                    : $"Rollback requested for workflow operation '{result!.OperationId}'.",
                Details = failed
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["errorKind"] = errorKind ?? "not-found",
                    }
                    : new Dictionary<string, string>(StringComparer.Ordinal),
            },
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
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
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        });

    private OperationHandle CreatePlatformAuthorityDeniedHandle(OperationPolicyContext context, string targetId)
    {
        var now = Clock.GetUtcNow();
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Workflow rollback requires a canonical operation instance."),
            OperationId = OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("Workflow rollback requires a canonical correlation identity."),
            Status = OperationHandleStatus.Denied,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = PlatformDeployAuthority.DenialMessage,
            ResourceIds = new Dictionary<string, string>(StringComparer.Ordinal),
            Result = new OperationResultSummary
            {
                Summary = $"Rollback of workflow operation '{targetId}' was denied.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["errorKind"] = PlatformDeployAuthority.DenialCode,
                },
            },
        };
    }

    protected abstract Task<WorkflowOperationRecord?> RollbackAsync(
        OperationRequest request,
        OperationPolicyContext context,
        string targetOperationId,
        CancellationToken cancellationToken);

    protected virtual void ValidateAdditionalParameters(OperationRequest request)
    {
    }

    protected static string Required(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required parameter '{name}' is missing.", nameof(request));

    protected static string? Optional(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) ? value : null;

}

internal sealed class DeployRollbackOperationExecutor(
    IServiceProvider services,
    TimeProvider clock) : WorkflowRollbackOperationExecutor(services, clock)
{
    public override string OperationId => WorkflowRollbackOperations.Deploy;

    protected override void ValidateAdditionalParameters(OperationRequest request)
    {
        _ = RequiredBoolean(request, WorkflowRollbackOperations.ApprovedDataAffecting);
        _ = RequiredBoolean(request, WorkflowRollbackOperations.ApprovedRequiresApproval);
    }

    protected override async Task<WorkflowOperationRecord?> RollbackAsync(
        OperationRequest request,
        OperationPolicyContext context,
        string targetOperationId,
        CancellationToken cancellationToken)
    {
        return await Services.GetRequiredService<DeployWorkflowService>().RequestRollbackAsync(
            targetOperationId,
            context.PrincipalId,
            Optional(request, WorkflowRollbackOperations.Reason),
            approvedMetadataReleaseIsDataAffecting: RequiredBoolean(
                request, WorkflowRollbackOperations.ApprovedDataAffecting),
            approvedMetadataReleaseRequiresApproval: RequiredBoolean(
                request, WorkflowRollbackOperations.ApprovedRequiresApproval),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool RequiredBoolean(OperationRequest request, string name)
        => bool.TryParse(Required(request, name), out var value)
            ? value
            : throw new ArgumentException($"Required parameter '{name}' must be a boolean.", nameof(request));
}

internal sealed class CoordinatedReleaseRollbackOperationExecutor(
    IServiceProvider services,
    TimeProvider clock) : WorkflowRollbackOperationExecutor(services, clock)
{
    public override string OperationId => WorkflowRollbackOperations.CoordinatedRelease;

    protected override Task<WorkflowOperationRecord?> RollbackAsync(
        OperationRequest request,
        OperationPolicyContext context,
        string targetOperationId,
        CancellationToken cancellationToken) => Services.GetRequiredService<CoordinatedReleaseControlService>().RequestRollbackAsync(
            targetOperationId,
            context.PrincipalId,
            Optional(request, WorkflowRollbackOperations.Reason),
            cancellationToken);
}
