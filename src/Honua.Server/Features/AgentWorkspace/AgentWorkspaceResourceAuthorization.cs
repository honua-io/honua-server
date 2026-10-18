// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>Resolves catalog locators through the same tenant and resource gates as map discovery.</summary>
internal static class AgentWorkspaceResourceAuthorization
{
    public static async Task<bool> CanReadAsync(HttpContext context, AgentResourceReference reference, CancellationToken cancellationToken)
    {
        if (reference.Kind != "layer" || !int.TryParse(reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var layerId)) return false;
        if (reference.ServiceId is null)
        {
            return (await context.RequestServices.GetRequiredService<ILayerAccessAuthorizer>()
                .AuthorizeLayerAsync(context.User, layerId, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed;
        }
        var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        // A saved reference binds the exact catalog identity, never an ambiguous display name.
        var service = snapshot.Graph.Services.FirstOrDefault(service => service.Metadata.Id == reference.ServiceId);
        if (service is null) return false;
        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, layerId);
        if (publication is null || !snapshot.IsRoutable(publication)) return false;
        var resource = snapshot.ResolveResource(publication);
        if (resource is null || !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service)) return false;
        return (await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service,
            AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed;
    }
    public static async Task<bool> CanReadTaskAsync(HttpContext context, AgentTaskReference? task, bool requireGeneration = false,
        AgentWorkspaceDocument? workspace = null)
    {
        if (task is null) return true;
        if (!AgentWorkspaceState.IsValidTaskReference(task)) return false;
        if (task.Kind == "publish-layer") return true; // Workspace membership protects workspace-owned forms.
        if (task.Kind == "local-process-workspace")
            return await LocalProcessWorkspaceAuthorization.CanReadTaskAsync(context, workspace, task, requireGeneration).ConfigureAwait(false);
        if (task.Kind == "workflow-package")
        {
            var version = await context.RequestServices.GetRequiredService<IWorkflowPackageStore>()
                .GetVersionAsync(task.Id, checked((int)task.Revision), context.RequestAborted).ConfigureAwait(false);
            var tenant = context.RequestServices.GetService<ITenantContext>();
            var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
            if (version?.Ownership is not { } ownership || tenant is null || actor is null ||
                ownership.TenantId != tenant.TenantId || version.PackageId != task.Id || version.Version != task.Revision ||
                version.PackageHash != task.ContentHash) return false;
            if (ownership.OwnerActorId == actor) return true;
            // Delegation comes only from the persisted workspace membership and owner.
            // A model-supplied package id, metadata tenant, or admin role cannot establish it.
            return workspace is not null && ownership.OwnerActorId == workspace.OwnerId &&
                context.User.HasClaim("permission", "agent:workspace") && context.User.FindFirst("api_key_id") is not null &&
                AgentWorkspaceState.CanRead(workspace, actor, tenant.TenantId, workspace.TargetId);
        }
        var draft = await context.RequestServices.GetRequiredService<IStudioPackageLifecycleService>()
            .GetDraftAsync(Guid.Parse(task.Id), context.RequestAborted).ConfigureAwait(false);
        if (draft is null || draft.PackageKey != task.PackageKey ||
            draft.Family is not (StudioPackageFamily.Map or StudioPackageFamily.App)) return false;
        var decision = await context.RequestServices.GetRequiredService<StudioEndpointAuthorization>()
            .AuthorizeAsync(context, StudioAuthorizationOperation.ReadDraft, draft.OwnerId, draft.TenantId,
                "studio-package-draft", draft.DraftId.ToString("D")).ConfigureAwait(false);
        if (!decision.IsAllowed) return false;
        if (requireGeneration && draft.Generation != task.Revision)
            throw new AgentWorkspaceConflictException("The Studio draft changed. Review its current generation before sending.");
        return true;
    }

}
