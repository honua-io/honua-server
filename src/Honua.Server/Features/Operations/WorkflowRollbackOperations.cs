// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Operations;

internal static class WorkflowRollbackOperations
{
    public const string Deploy = "control-plane.deploy.rollback";
    public const string CoordinatedRelease = "control-plane.coordinated-release.rollback";
    public const string TargetOperationId = "targetOperationId";
    public const string RequestedBy = "requestedBy";
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
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.DeploymentScope,
            SideEffectClass = OperationSideEffectClass.DestroysState,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = false,
        },
        InputSchema =
        [
            new OperationParameterDescriptor
            {
                Name = TargetOperationId,
                Title = "Target workflow operation id",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
            },
        ],
        OutputSchema = [],
    };
}

internal abstract class WorkflowRollbackOperationExecutor(TimeProvider clock) : IOperationExecutor
{
    protected TimeProvider Clock { get; } = clock;
    public abstract string OperationId { get; }

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        _ = Required(request, WorkflowRollbackOperations.TargetOperationId);
        return Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var targetId = Required(request, WorkflowRollbackOperations.TargetOperationId);
        var result = await RollbackAsync(request, targetId, cancellationToken).ConfigureAwait(false);
        var now = Clock.GetUtcNow();
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Workflow rollback requires a canonical operation instance."),
            OperationId = OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("Workflow rollback requires a canonical correlation identity."),
            Status = result is null ? OperationHandleStatus.Failed : OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = result is null ? $"Workflow operation '{targetId}' was not found." : null,
            ResourceIds = result is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workflowOperationId"] = result.OperationId,
                },
            Result = new OperationResultSummary
            {
                Summary = result is null
                    ? $"Workflow operation '{targetId}' was not found."
                    : $"Rollback requested for workflow operation '{result.OperationId}'.",
                Details = result is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal) { ["errorKind"] = "not-found" }
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

    protected abstract Task<WorkflowOperationRecord?> RollbackAsync(
        OperationRequest request,
        string targetOperationId,
        CancellationToken cancellationToken);

    protected static string Required(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required parameter '{name}' is missing.", nameof(request));

    protected static string? Optional(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) ? value : null;
}

internal sealed class DeployRollbackOperationExecutor(
    IServiceProvider services,
    TimeProvider clock) : WorkflowRollbackOperationExecutor(clock)
{
    public override string OperationId => WorkflowRollbackOperations.Deploy;

    protected override Task<WorkflowOperationRecord?> RollbackAsync(
        OperationRequest request,
        string targetOperationId,
        CancellationToken cancellationToken) => services.GetRequiredService<DeployWorkflowService>().RequestRollbackAsync(
            targetOperationId,
            Optional(request, WorkflowRollbackOperations.RequestedBy),
            Optional(request, WorkflowRollbackOperations.Reason),
            ParseNullableBoolean(request, WorkflowRollbackOperations.ApprovedDataAffecting),
            ParseNullableBoolean(request, WorkflowRollbackOperations.ApprovedRequiresApproval),
            cancellationToken);

    private static bool? ParseNullableBoolean(OperationRequest request, string name)
        => bool.TryParse(Optional(request, name), out var value) ? value : null;
}

internal sealed class CoordinatedReleaseRollbackOperationExecutor(
    IServiceProvider services,
    TimeProvider clock) : WorkflowRollbackOperationExecutor(clock)
{
    public override string OperationId => WorkflowRollbackOperations.CoordinatedRelease;

    protected override Task<WorkflowOperationRecord?> RollbackAsync(
        OperationRequest request,
        string targetOperationId,
        CancellationToken cancellationToken) => services.GetRequiredService<CoordinatedReleaseControlService>().RequestRollbackAsync(
            targetOperationId,
            Optional(request, WorkflowRollbackOperations.RequestedBy),
            Optional(request, WorkflowRollbackOperations.Reason),
            cancellationToken);
}
