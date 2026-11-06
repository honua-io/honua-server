// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

internal sealed class RouteTagOutputCachePolicy : IOutputCachePolicy
{
    public async ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        AddRouteTags(context);
        var snapshot = GeoServicesServicePath.FromRequest(context.HttpContext)?.Snapshot;
        if (snapshot is null && context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<GeoServicesServiceRoutes.CatalogRoute>() is not null)
        {
            try
            {
                snapshot = await context.HttpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
                    .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ServiceResourceValidationHelpers.IsCatalogStorageUnavailable(exception))
            {
                context.EnableOutputCaching = false;
                context.AllowCacheLookup = false;
                context.AllowCacheStorage = false;
                return;
            }
        }
        if (snapshot is not null)
        {
            context.CacheVaryByRules.VaryByValues["honua-service-revision"] = snapshot.Etag;
            context.CacheVaryByRules.VaryByValues["honua-service-tenant"] = TenantScopeHelpers.ResolveRequestTenantId(context.HttpContext) ?? string.Empty;
        }
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        AddRouteTags(context);
        return ValueTask.CompletedTask;
    }

    private static void AddRouteTags(OutputCacheContext context)
    {
        var routeValues = context.HttpContext.Request.RouteValues;

        AddTag(context, routeValues, "serviceId", "service");
        AddTag(context, routeValues, "layerId", "layer");
        AddTag(context, routeValues, "collectionId", "collection");
        AddTag(context, routeValues, "sceneId", "scene");
    }

    private static void AddTag(OutputCacheContext context, RouteValueDictionary routeValues, string routeKey, string tagPrefix)
    {
        if (!routeValues.TryGetValue(routeKey, out var value) || value == null)
        {
            return;
        }

        var tagValue = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(tagValue))
        {
            return;
        }

        context.Tags.Add($"{tagPrefix}:{tagValue.ToLowerInvariant()}");
    }
}
