// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal static class WorkTaskAccess
{
    private static readonly object RequestCacheKey = new();

    public static async Task<AssetServiceGrant> RequireAsync(HttpContext context, string? serviceId, Guid? profileId)
    {
        if (context.Items[RequestCacheKey] is not Dictionary<(string?, Guid?), AssetServiceGrant> cache)
        {
            cache = [];
            context.Items[RequestCacheKey] = cache;
        }

        var key = (serviceId, profileId);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var grant = await ResolveAsync(context, serviceId, profileId).ConfigureAwait(false);
        cache[key] = grant;
        return grant;
    }

    private static async Task<AssetServiceGrant> ResolveAsync(HttpContext context, string? serviceId, Guid? profileId)
    {
        var grant = await AssetServiceAccess.ResolveServiceAsync(context, serviceId).ConfigureAwait(false);
        if (grant.Denied is not null)
        {
            return grant;
        }

        if (!TenantScopeHelpers.IsTenantVisible(context, null, grant.Service))
        {
            return Missing(context);
        }

        if (profileId is Guid id)
        {
            return await RequireProfileAsync(context, grant, id).ConfigureAwait(false);
        }

        var denied = await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context, grant.Service!, context.RequestAborted).ConfigureAwait(false);
        return denied is null ? grant : AssetServiceGrant.Deny(denied);
    }

    private static async Task<AssetServiceGrant> RequireProfileAsync(HttpContext context, AssetServiceGrant grant, Guid profileId)
    {
        var profiles = context.RequestServices.GetRequiredService<IAssetProfileStore>();
        var profile = await profiles.GetAsync(profileId, context.RequestAborted).ConfigureAwait(false);
        if (profile is null || !AssetServiceAccess.Matches(grant.Service!, profile.ServiceId))
        {
            return Missing(context);
        }

        return await AssetServiceAccess.RequireLayerEditorAsync(context, grant, profile.LayerId).ConfigureAwait(false);
    }

    private static AssetServiceGrant Missing(HttpContext context)
        => AssetServiceGrant.Deny(ProblemDetailsHelpers.CreateAdminProblem(
            context, StatusCodes.Status404NotFound, "The task target was not found."));
}
