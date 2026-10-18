// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>Reads a persisted workflow operation under its original turn and current caller authority.</summary>
internal static class WorkflowPackageOperationStatus
{
    internal static async Task<OperationHandle?> ReadAsync(HttpContext context, string workspaceId,
        string operationInstanceId, CancellationToken cancellationToken)
    {
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null || !context.User.HasClaim("permission", "agent:workspace")) return null;
        var services = context.RequestServices;
        var handle = await services.GetRequiredService<IOperationInstanceStore>()
            .GetAsync(operationInstanceId, cancellationToken).ConfigureAwait(false);
        if (handle?.OperationId != WorkflowPackageGovernanceOperation.OperationId ||
            string.IsNullOrWhiteSpace(handle.ProposalId)) return null;
        var proposal = await services.GetRequiredService<IOperationProposalStore>()
            .GetAsync(handle.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.RequestedBy != actor || proposal.OperationId != handle.OperationId ||
            proposal.Audit.OperationInstanceId != operationInstanceId || string.IsNullOrWhiteSpace(proposal.Plan.ExecutionPayload) ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) return null;
        var payload = JsonSerializer.Deserialize(proposal.Plan.ExecutionPayload,
            WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageApprovalPayload);
        if (payload is null || payload.ActorId != actor || payload.WorkspaceId != workspaceId ||
            handle.TenantId != payload.TenantId) return null;

        // The caller may have moved to another tab or turn. Reauthorize the persisted
        // original selection without replacing the ambient request or human principal.
        var original = new DefaultHttpContext { User = context.User, RequestServices = services, RequestAborted = cancellationToken };
        original.Request.Headers["X-Honua-Workspace"] = workspaceId;
        original.Request.Headers["X-Honua-Turn"] = payload.TurnId;
        var accessor = new OriginalTurnAccessor { HttpContext = original };
        var store = services.GetRequiredService<IAgentWorkspaceStore>();
        var turns = new AgentWorkspaceTurnAuthorizer(store, accessor, services.GetRequiredService<IConfiguration>());
        var authorization = new WorkflowPackageAuthorization(accessor, services.GetRequiredService<ITenantContext>(), turns);
        var access = await authorization.ResolveAsync(context.User, cancellationToken, workspaceId, payload.TurnId).ConfigureAwait(false);
        var binding = access.TurnBinding;
        if (binding is null || binding.OwnerActorId != payload.OwnerActorId || binding.TenantId != payload.TenantId ||
            binding.TargetId != payload.TargetId || binding.ReleaseId != payload.ReleaseId) return null;
        var packages = services.GetRequiredService<IWorkflowPackageStore>();
        var package = await packages.GetPackageAsync(payload.Intent.PackageId, cancellationToken).ConfigureAwait(false);
        var version = await packages.GetVersionAsync(payload.Intent.PackageId, payload.Intent.Version, cancellationToken).ConfigureAwait(false);
        if (package is null || version is null || !access.CanAccess(package.Ownership) || !access.CanAccess(version.Ownership) ||
            version.PackageHash != payload.Intent.PackageHash) return null;

        // Reading status never repeats publication, scheduling or deletion. An interrupted
        // operation stays unresolved until its canonical executor records completion.
        return handle;
    }

    private sealed class OriginalTurnAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
