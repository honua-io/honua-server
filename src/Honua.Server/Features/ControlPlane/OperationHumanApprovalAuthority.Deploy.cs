// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal sealed partial class OperationHumanApprovalAuthority
{
    public async Task<OperationHumanApprovalReview> OpenDeployReviewAsync(
        WorkflowOperationRecord operation, string action, HttpContext context)
    {
        var target = DeployTarget(operation, action);
        var grant = await OpenCapabilityAsync(target, context).ConfigureAwait(false);
        return new OperationHumanApprovalReview
        {
            TenantId = target.TenantId,
            ProposalId = operation.OperationId,
            ProposalVersion = operation.Version,
            PlanHash = target.BindingDigest,
            ExpiresAt = grant.ExpiresAt,
            Capability = grant.Capability,
        };
    }

    public async Task<DeployHumanApproval> VerifyDeployAsync(
        WorkflowOperationRecord operation, string action,
        OperationProposalApprovalContext approval, CancellationToken cancellationToken)
    {
        var target = DeployTarget(operation, action);
        var verified = await VerifyCapabilityAsync(target, approval, cancellationToken).ConfigureAwait(false);
        return new DeployHumanApproval
        {
            WorkflowId = operation.OperationId,
            WorkflowVersion = operation.Version,
            Action = action,
            TenantId = target.TenantId,
            ActorId = verified.ActorId,
            Channel = verified.Channel,
            SessionReference = verified.SessionReference,
            CapabilityReference = verified.CapabilityReference,
            PlanHash = target.BindingDigest,
            ApprovedAt = verified.ApprovedAt
        };
    }

    private HumanApprovalTarget DeployTarget(WorkflowOperationRecord operation, string action)
    {
        if (operation.Kind != WorkflowOperationKind.Deploy || operation.Deploy is null
            || action is not ("submit" or "promote")
            || (action == "submit" && operation.Status is not (WorkflowOperationStatus.Planned or WorkflowOperationStatus.AwaitingApproval))
            || (action == "promote" && operation.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling)))
            throw new HumanApprovalRequiredException();

        var tenantOptions = requestContext.HttpContext?.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value;
        var binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            operation, OperationHumanApprovalJsonContext.Default.WorkflowOperationRecord)));
        return new HumanApprovalTarget("deploy-" + action, operation.OperationId, operation.Version,
            operation.Audit.TenantId, binding, null,
            principal => PlatformDeployAuthority.IsAuthorized(principal, tenantOptions)
                && OperationApprovalAuthorization.PermitsOperationScope(principal, new OperationApprovalScopeRequest(OperationClass.Deploy)));
    }
}
