// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Classic;

internal readonly record struct CachedLayerIdentity(
    int StorageLayerId,
    MetadataV2Object Resource,
    MetadataV2Layer Publication);

internal sealed class WmtsCapabilitiesCacheRequest
{
    internal required HttpContext Context { get; init; }
    internal required string SnapshotEtag { get; init; }
    internal required string ServiceId { get; init; }
    internal required string ResponseMimeType { get; init; }
    internal required int SectionsMask { get; init; }
    internal required int MaxZoom { get; init; }
}

internal sealed class WmsCapabilitiesCacheRequest
{
    internal required HttpContext Context { get; init; }
    internal required string SnapshotEtag { get; init; }
    internal required string ServiceId { get; init; }
    internal required string Version { get; init; }
}

internal sealed class WfsCapabilitiesCacheRequest
{
    internal required string SnapshotEtag { get; init; }
    internal required string BaseUrl { get; init; }
    internal required int SectionsMask { get; init; }
}

internal static class CapabilitiesDocumentCacheHelper
{
    internal static async Task<IResult> GetOrBuildAsync(
        HttpContext context,
        string? cacheKey,
        Func<Task<PooledUtf8ContentResult>> buildAsync)
    {
        if (cacheKey is null ||
            MetadataDocumentByteCache.HasQueryToken(context.Request) ||
            context.RequestServices.GetService<MetadataDocumentByteCache>() is not { } docCache)
        {
            return await buildAsync().ConfigureAwait(false);
        }

        var configGeneration = await MetadataDocumentByteCache
            .ResolveConfigGenerationAsync(context, context.RequestAborted)
            .ConfigureAwait(false);
        var stamp = docCache.CaptureStamp(configGeneration);
        if (docCache.Get(stamp, cacheKey) is { } hit)
        {
            return hit.ToResult();
        }

        var result = await buildAsync().ConfigureAwait(false);
        CachedMetadataDocument cached;
        try
        {
            cached = MetadataDocumentByteCache.CreateDocument(
                result.Content.Span,
                result.ContentType,
                context.RequestServices.GetService<IETagService>());
        }
        finally
        {
            await result.ReleaseAsync().ConfigureAwait(false);
        }

        return docCache.Set(stamp, cacheKey, cached).ToResult();
    }

    internal static string BuildWmtsKey(WmtsCapabilitiesCacheRequest request, ReadOnlySpan<CachedLayerIdentity> layers)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("wmts|").Append(request.SnapshotEtag).Append('|').Append(request.ServiceId)
                .Append('|').Append(GeoServicesServiceUrls.RequestedProtocol(request.Context, "WMTS"))
                .Append('|').Append(request.ResponseMimeType)
                .Append('|').Append(request.SectionsMask)
                .Append('|').Append(request.MaxZoom);
            AppendLayers(sb, "|v:", layers);
            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    internal static string BuildWmsKey(
        WmsCapabilitiesCacheRequest request,
        ReadOnlySpan<CachedLayerIdentity> accessibleLayers,
        ReadOnlySpan<CachedLayerIdentity> challengeLayers)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("wms|").Append(request.SnapshotEtag).Append('|').Append(request.ServiceId)
                .Append('|').Append(GeoServicesServiceUrls.RequestedProtocol(request.Context, "WMS"))
                .Append('|').Append(request.Version);
            AppendLayers(sb, "|a:", accessibleLayers);
            AppendLayers(sb, "|c:", challengeLayers);
            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    private static void AppendLayers(StringBuilder sb, string marker, ReadOnlySpan<CachedLayerIdentity> layers)
    {
        sb.Append(marker);
        foreach (var layer in layers)
        {
            sb.Append('|').Append(layer.StorageLayerId)
                .Append(':').Append(layer.Resource.Metadata.Id)
                .Append(':').Append(layer.Resource.Metadata.Generation.GetValueOrDefault())
                .Append(':').Append(layer.Publication.Metadata.Id)
                .Append(':').Append(layer.Publication.Metadata.Generation.GetValueOrDefault());
        }
    }
}
