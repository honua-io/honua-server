// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Server.Features.Forms;

internal static class FormRuntimeAccess
{
    public static async Task<bool> CanReadAsync(HttpContext context, FormPackageVersion version)
    {
        if (!string.Equals(version.Status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var metadata = context.RequestServices.GetRequiredService<IFormTargetMetadataResolver>();
        var target = await metadata.ResolveAsync(version.Package.Target, context.RequestAborted).ConfigureAwait(false);
        if (target.Service is null || target.Publication is null || target.Resource is null)
        {
            return false;
        }

        var authorizer = context.RequestServices.GetRequiredService<ILayerAccessAuthorizer>();
        var access = await authorizer.AuthorizePublicationAsync(
            context.User,
            target.Publication,
            target.Resource,
            target.Service,
            AuthorizationOperation.Query,
            context.RequestAborted).ConfigureAwait(false);
        return access.IsAllowed;
    }
}
