// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal sealed partial class OperationHumanApprovalAuthority
{
    public async Task<CoordinatedReleaseHumanReview> OpenGateReviewAsync(
        WorkflowOperationRecord operation, CoordinatedReleaseStep gate, HttpContext context)
    {
        ValidateGateTarget(operation, gate);
        var target = GateTarget(operation, gate);
        var grant = await OpenCapabilityAsync(target, context).ConfigureAwait(false);
        return new CoordinatedReleaseHumanReview
        {
            WorkflowId = operation.OperationId,
            WorkflowVersion = operation.Version,
            Gate = gate,
            PlanHash = target.BindingDigest,
            ExpiresAt = grant.ExpiresAt,
            Capability = grant.Capability,
        };
    }

    public async Task<CoordinatedReleaseHumanApproval> VerifyGateAsync(
        WorkflowOperationRecord operation, CoordinatedReleaseStep gate,
        OperationProposalApprovalContext approval, CancellationToken cancellationToken)
    {
        ValidateGateTarget(operation, gate);
        var target = GateTarget(operation, gate);
        var verified = await VerifyCapabilityAsync(target, approval, cancellationToken).ConfigureAwait(false);
        return new CoordinatedReleaseHumanApproval
        {
            Version = CoordinatedReleaseHumanApproval.CurrentVersion,
            WorkflowId = operation.OperationId,
            WorkflowVersion = operation.Version,
            Gate = gate,
            TenantId = target.TenantId,
            ActorId = verified.ActorId,
            Channel = OperationHumanApprovalEvidence.InteractiveOidcChannel,
            SessionReference = verified.SessionReference,
            CapabilityReference = verified.CapabilityReference,
            PlanHash = target.BindingDigest,
            ApprovedAt = verified.ApprovedAt,
        };
    }

    private HumanApprovalTarget GateTarget(WorkflowOperationRecord operation, CoordinatedReleaseStep gate)
    {
        var tenantOptions = requestContext.HttpContext?.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value;
        var kind = gate == CoordinatedReleaseStep.ContainerRollout ? OperationClass.Deploy : OperationClass.MetadataRelease;
        return new HumanApprovalTarget("coordinated-release-gate", operation.OperationId, operation.Version,
            operation.Audit.TenantId, CoordinatedReleaseApprovalBinding.ComputePlanHash(operation, gate), null,
            principal => PlatformDeployAuthority.IsAuthorized(principal, tenantOptions)
                && OperationApprovalAuthorization.PermitsOperationScope(principal, new OperationApprovalScopeRequest(kind)));
    }

    private static void ValidateGateTarget(WorkflowOperationRecord operation, CoordinatedReleaseStep gate)
    {
        if (operation.Kind != WorkflowOperationKind.CoordinatedRelease
            || operation.CoordinatedRelease is not { } context
            || gate is not (CoordinatedReleaseStep.ContainerRollout or CoordinatedReleaseStep.MetadataAndSchema)
            || context.CurrentStep != gate || operation.Status != WorkflowOperationStatus.AwaitingApproval
            || operation.Audit.TenantId != context.RequesterTenantId)
            throw new HumanApprovalRequiredException();
    }
}
