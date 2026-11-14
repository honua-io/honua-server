// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Assets;

internal sealed class AssetProfileReadService(IAssetProfileStore store, AssetCompletionCapabilities capabilities)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<IResult> ListAsync(HttpContext context)
    {
        var grant = await AssetServiceAccess.ResolveServiceAsync(context, context.Request.Query["serviceId"].ToString()).ConfigureAwait(false);
        if (grant.Denied is { } denied)
        {
            return denied;
        }

        var cursor = context.Request.Query["cursor"].ToString();
        Guid? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!Guid.TryParse(cursor, out var parsed))
            {
                return AssetCommandHttp.Rejected(context, "cursor", "cursor is not valid.");
            }

            after = parsed;
        }

        var limit = int.TryParse(context.Request.Query["limit"], out var requested) ? Math.Clamp(requested, 1, MaxLimit) : DefaultLimit;
        var result = await store.ListAsync(new AssetProfileQuery(grant.ServiceId, after, limit), context.RequestAborted).ConfigureAwait(false);
        var visible = new List<AssetProfile>();
        foreach (var profile in result.Items)
        {
            var access = await AssetServiceAccess.RequireLayerEditorAsync(context, grant, profile.LayerId).ConfigureAwait(false);
            if (access.Denied is null)
            {
                visible.Add(await capabilities.DescribeAsync(profile, context.RequestAborted).ConfigureAwait(false));
            }
        }

        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(result with { Items = visible }, AssetsJsonContext.Default.AssetProfileListResult);
    }

    public async Task<IResult> GetAsync(HttpContext context, Guid profileId)
    {
        var profile = await store.GetAsync(profileId, context.RequestAborted).ConfigureAwait(false);
        if (profile is null || (await AssetServiceAccess.RequireLayerEditorAsync(context, profile.ServiceId, profile.LayerId).ConfigureAwait(false)).Denied is not null)
        {
            return NotFound(context);
        }

        profile = await capabilities.DescribeAsync(profile, context.RequestAborted).ConfigureAwait(false);
        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(profile, AssetsJsonContext.Default.AssetProfile);
    }

    private static IResult NotFound(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The asset profile was not found.");
}
