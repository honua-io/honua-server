// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Resources;

internal static class StudioDeploymentReadAuthority
{
    public static async Task<bool> CanReadAsync(
        HttpContext context,
        Deployment deployment,
        CancellationToken cancellationToken)
    {
        if (deployment.Source.Kind != DeploymentSourceKind.StudioPublishedVersion)
        {
            return true;
        }

        if (deployment.Source.StudioItemId is not Guid itemId
            || deployment.Source.StudioVersionId is not Guid versionId)
        {
            return false;
        }

        var lifecycle = context.RequestServices.GetService<IStudioPackageLifecycleService>();
        var authorization = context.RequestServices.GetService<IStudioAuthorizationService>();
        if (lifecycle is null || authorization is null)
        {
            return false;
        }

        // The deployment's audit/owner fields are historical provenance. The Studio item
        // remains the authority for the caller's current ownership and tenant boundary.
        var item = await lifecycle.GetPointersAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return false;
        }

        var principal = context.User;
        var decision = await authorization.AuthorizeAsync(
            principal,
            authorization.ResolveCallerId(principal),
            StudioAuthorizationOperation.ReadContentItem,
            item.OwnerId,
            item.TenantId,
            isPubliclyReadable: item.PublishedVersionId == versionId,
            resourceId: itemId.ToString("D"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed;
    }
}
