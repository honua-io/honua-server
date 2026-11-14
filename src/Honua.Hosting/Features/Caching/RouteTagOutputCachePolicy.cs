// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

// Every cached response is derived from the catalog, and an anonymous caller's access is decided by the catalog alone.
// Keying each entry by the catalog revision means another node's change to a layer, its access policy or its style
// retires this node's entries as soon as this node sees the new revision, instead of when their lifetime ends.
internal sealed class RouteTagOutputCachePolicy : IOutputCachePolicy
{
    internal const string CatalogRevisionKey = "honua-service-revision";

    public async ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        AddRouteTags(context);
        if (!MayUseOutputCache(context.HttpContext))
        {
            return;
        }

        var snapshot = GeoServicesServicePath.FromRequest(context.HttpContext)?.Snapshot;
        if (snapshot is null)
        {
            try
            {
                snapshot = await CurrentCatalogAsync(context.HttpContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ServiceResourceValidationHelpers.IsCatalogStorageUnavailable(exception))
            {
                DisableCaching(context);
                return;
            }
        }

        if (context.HttpContext.RequestServices.GetService<IConfigurationGenerationSource>() is { } generations)
        {
            context.CacheVaryByRules.VaryByValues["honua-configuration-generation"] =
                (await generations.GetGenerationAsync(cancellationToken).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture);
        }

        if (snapshot is not null)
        {
            context.CacheVaryByRules.VaryByValues[CatalogRevisionKey] = snapshot.Etag;
            context.CacheVaryByRules.VaryByValues["honua-service-tenant"] = TenantScopeHelpers.ResolveRequestTenantId(context.HttpContext) ?? string.Empty;
            OutputCacheStatus.UseCatalogRevision(context.HttpContext, snapshot.Etag);
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

    // Only a GET to an endpoint that opted into output caching can be looked up or stored; the base policy also sees
    // every other request, and those must not pay for a catalog read.
    private static bool MayUseOutputCache(HttpContext context)
        => HttpMethods.IsGet(context.Request.Method) && OutputCacheStatus.RouteUsesOutputCache(context);

    private static async ValueTask<MetadataV2GraphSnapshot?> CurrentCatalogAsync(HttpContext context, CancellationToken cancellationToken)
        => context.RequestServices.GetService<IMetadataV2GraphProvider>() is { } provider
            ? await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false)
            : null;

    private static void DisableCaching(OutputCacheContext context)
    {
        context.EnableOutputCaching = false;
        context.AllowCacheLookup = false;
        context.AllowCacheStorage = false;
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
