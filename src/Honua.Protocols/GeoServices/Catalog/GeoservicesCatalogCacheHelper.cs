// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.Catalog;

internal readonly record struct DirectoryCacheSlot(
    MetadataDocumentByteCache Cache,
    MetadataCacheStamp Stamp,
    string CacheKey);

internal static class GeoservicesCatalogCacheHelper
{
    private const string JsonUtf8ContentType = "application/json; charset=utf-8";

    internal static bool CanCacheAnonymousDirectory(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        return !BypassOutputCacheOnCredentialedRequestPolicy.CarriesCredential(context);
    }

    internal static string BuildAnonymousDirectoryKey(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        ILicenseStatusProvider licenseStatusProvider)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            var offlineSync = CapabilityFlagOptions.IsExperimentalEnabled(
                context.RequestServices.GetRequiredService<IConfiguration>(),
                "sync.offline");
            var geocoding = context.RequestServices.GetService<IOptions<GeocodingConfiguration>>()?.Value;
            sb.Append("gs-dir-anon|").Append(snapshot.Etag)
                .Append('|').Append(BaseUrlResolver.GetBaseUrl(context))
                .Append('|').Append(TenantScopeHelpers.ResolveRequestTenantId(context) ?? string.Empty)
                .Append('|').Append((int)licenseStatusProvider.GetCurrentStatus().Edition)
                .Append('|').Append(offlineSync ? '1' : '0')
                .Append('|').Append(geocoding is { Enabled: true } ? geocoding.DefaultProvider : string.Empty)
                .Append('|').Append(geocoding is { Enabled: true } ? geocoding.LocatorName : string.Empty);
            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    internal static async ValueTask<(MetadataV2GraphSnapshot? Snapshot, DirectoryCacheSlot? Slot, CachedMetadataDocument? Hit)>
        TryLookupAnonymousDirectoryAsync(
            HttpContext context,
            IMetadataV2GraphProvider graphProvider,
            ILicenseStatusProvider licenseStatusProvider)
    {
        if (!CanCacheAnonymousDirectory(context) ||
            context.RequestServices.GetService<MetadataDocumentByteCache>() is not { } cache)
        {
            return (null, null, null);
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var rawKey = BuildAnonymousDirectoryKey(context, snapshot, licenseStatusProvider);
        var cacheKey = MetadataDocumentByteCache.ScopeKeyForRequest(context, rawKey);
        var generation = await MetadataDocumentByteCache
            .ResolveConfigGenerationAsync(context, context.RequestAborted)
            .ConfigureAwait(false);
        var stamp = cache.CaptureStamp(generation);
        return (snapshot, new DirectoryCacheSlot(cache, stamp, cacheKey), cache.Get(stamp, cacheKey));
    }

    internal static IResult StoreAndReturn(
        HttpContext context,
        DirectoryCacheSlot slot,
        ServicesDirectoryResponse response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            response,
            GeoservicesCatalogJsonContext.Default.ServicesDirectoryResponse);
        var etag = context.RequestServices.GetService<IETagService>()?.ComputeETag(bytes);
        var document = new CachedMetadataDocument(bytes, JsonUtf8ContentType, etag);
        return slot.Cache.Set(slot.Stamp, slot.CacheKey, document).ToResult();
    }
}
