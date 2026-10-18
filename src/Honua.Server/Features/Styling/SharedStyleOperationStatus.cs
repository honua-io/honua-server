// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Styling;

/// <summary>Read-only original-command recovery, including from a later chat turn.</summary>
internal static class SharedStyleOperationStatus
{
    internal static async Task<OperationHandle?> ReadAsync(HttpContext context, string workspaceId,
        string operationInstanceId, CancellationToken cancellationToken)
    {
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null || !context.User.HasClaim("permission", "agent:workspace")) return null;
        var services = context.RequestServices;
        var instances = services.GetRequiredService<IOperationInstanceStore>();
        var handle = await instances.GetAsync(operationInstanceId, cancellationToken).ConfigureAwait(false);
        if (handle?.OperationId != SharedStyleEditOperation.OperationId || string.IsNullOrWhiteSpace(handle.ProposalId)) return null;
        var proposal = await services.GetRequiredService<IOperationProposalStore>().GetAsync(handle.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.RequestedBy != actor || proposal.OperationId != handle.OperationId ||
            proposal.Audit.OperationInstanceId != operationInstanceId || string.IsNullOrWhiteSpace(proposal.Plan.ExecutionPayload) ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) return null;
        var payload = JsonSerializer.Deserialize(proposal.Plan.ExecutionPayload,
            SharedStyleGovernanceJsonContext.Default.SharedStyleApprovalPayload);
        if (payload is null || payload.ActorId != actor || payload.WorkspaceId != workspaceId || handle.TenantId != payload.TenantId) return null;
        var store = services.GetRequiredService<IAgentWorkspaceStore>();
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || !AgentWorkspaceState.CanRead(document, actor, payload.TenantId,
                await store.GetTargetIdAsync().ConfigureAwait(false)) || document.OwnerId != payload.OwnerActorId ||
            document.TargetId != payload.TargetId || document.TenantId != payload.TenantId ||
            !document.Turns.TryGetValue(payload.TurnId, out var originalTurn) ||
            originalTurn.Context.Task is not { Kind: "shared-style" } task || task.Id != payload.Impact.StyleId ||
            task.Revision != payload.Impact.StyleVersion || task.ContentHash != payload.Impact.ContentHash ||
            task.ImpactHash != payload.Impact.ImpactHash) return null;
        // This is not a mutation using the new turn. Authorize the persisted original
        // turn freshly, without substituting a later tab's current selection.
        var original = new DefaultHttpContext { User = context.User, RequestServices = services, RequestAborted = cancellationToken };
        original.Request.Headers["X-Honua-Workspace"] = workspaceId;
        original.Request.Headers["X-Honua-Turn"] = payload.TurnId;
        var binding = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(original, context.User,
            workspaceId, payload.TurnId, cancellationToken).ConfigureAwait(false);
        if (binding.Binding.TargetId != payload.TargetId || binding.Binding.ReleaseId != payload.ReleaseId ||
            binding.Binding.OwnerActorId != payload.OwnerActorId || binding.Binding.TenantId != payload.TenantId) return null;
        await services.GetRequiredService<IOperationGateway>().ReconcileCommittedReceiptAsync(proposal.ProposalId,
            context.User, cancellationToken).ConfigureAwait(false);
        return await instances.GetAsync(operationInstanceId, cancellationToken).ConfigureAwait(false);
    }
}
