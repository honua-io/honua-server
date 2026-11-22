// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features;

internal readonly record struct OgcCollectionCacheEntry(
    MetadataV2Layer Publication,
    MetadataV2Service? Service,
    MetadataV2Object Resource);

internal sealed class CollectionsResponseFormatArgs
{
    internal required string CacheKey { get; init; }
    internal required Func<Task<Collections>> BuildAsync { get; init; }
}

internal sealed class CollectionResponseFormatArgs
{
    internal required string CacheKey { get; init; }
    internal required Func<Task<CollectionInfo>> BuildAsync { get; init; }
}

internal sealed class CollectionsBuildArgs
{
    internal required HttpRequest Request { get; init; }
    internal required string BaseUrl { get; init; }
    internal required string OutputFormat { get; init; }
    internal required IReadOnlyList<(MetadataV2Layer Publication, MetadataV2Service Service, MetadataV2Object Resource)> VisiblePublications { get; init; }
    internal required MetadataV2GraphSnapshot Snapshot { get; init; }
    internal required IFeatureReader FeatureReader { get; init; }
    internal required ICrsRegistry CrsRegistry { get; init; }
    internal required ICoordinateTransformService CoordinateTransformService { get; init; }
    internal required CancellationToken CancellationToken { get; init; }
}

internal sealed class SingleCollectionBuildArgs
{
    internal required HttpRequest Request { get; init; }
    internal required string BaseUrl { get; init; }
    internal required string CollectionId { get; init; }
    internal required string OutputFormat { get; init; }
    internal required MetadataV2Object Resource { get; init; }
    internal required MetadataV2Layer Publication { get; init; }
    internal required MetadataV2Service? Service { get; init; }
    internal required MetadataV2GraphSnapshot Snapshot { get; init; }
    internal required IFeatureReader FeatureReader { get; init; }
    internal required ICrsRegistry CrsRegistry { get; init; }
    internal required ICoordinateTransformService CoordinateTransformService { get; init; }
    internal required CancellationToken CancellationToken { get; init; }
}

internal static class CollectionsCacheHelper
{
    internal static string BuildCollectionsKey(
        MetadataV2GraphSnapshot snapshot,
        HttpContext context,
        IReadOnlyList<(MetadataV2Layer Publication, MetadataV2Service Service, MetadataV2Object Resource)> visiblePublications)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("ogc-collections|").Append(snapshot.Etag)
                .Append('|').Append(BaseUrlResolver.GetBaseUrl(context))
                .Append('|').Append(context.Request.QueryString.Value ?? string.Empty);

            foreach (var (publication, service, resource) in visiblePublications)
            {
                sb.Append('|').Append(publication.Metadata.Id)
                    .Append(':').Append(publication.Metadata.Generation.GetValueOrDefault())
                    .Append(':').Append(service.Metadata.Id)
                    .Append(':').Append(service.Metadata.Generation.GetValueOrDefault())
                    .Append(':').Append(resource.Metadata.Id)
                    .Append(':').Append(resource.Metadata.Generation.GetValueOrDefault());
            }

            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    internal static string BuildCollectionKey(
        MetadataV2GraphSnapshot snapshot,
        HttpContext context,
        OgcCollectionCacheEntry entry)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("ogc-collection|").Append(snapshot.Etag)
                .Append('|').Append(BaseUrlResolver.GetBaseUrl(context))
                .Append('|').Append(context.Request.Path.Value ?? string.Empty)
                .Append('|').Append(context.Request.QueryString.Value ?? string.Empty)
                .Append('|').Append(entry.Publication.Metadata.Id)
                .Append(':').Append(entry.Publication.Metadata.Generation.GetValueOrDefault())
                .Append(':').Append(entry.Service?.Metadata.Id ?? string.Empty)
                .Append(':').Append(entry.Service?.Metadata.Generation.GetValueOrDefault() ?? 0)
                .Append(':').Append(entry.Resource.Metadata.Id)
                .Append(':').Append(entry.Resource.Metadata.Generation.GetValueOrDefault());

            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    internal static async Task<IResult> FormatCollectionsResponseAsync(
        HttpContext context,
        string outputFormat,
        CollectionsResponseFormatArgs args)
    {
        if (string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase))
        {
            return await MetadataDocumentByteCache.GetOrBuildJsonAsync(
                context,
                new JsonMetadataCacheRequest<Collections>
                {
                    CacheKey = args.CacheKey,
                    TypeInfo = OgcJsonContext.Default.Collections,
                    BuildAsync = args.BuildAsync
                }).ConfigureAwait(false);
        }

        var response = await args.BuildAsync().ConfigureAwait(false);
        return OgcCommonUtilities.FormatMetadataResponse(response, OgcJsonContext.Default.Collections, outputFormat, "Collections");
    }

    internal static async Task<IResult> FormatCollectionResponseAsync(
        HttpContext context,
        string outputFormat,
        CollectionResponseFormatArgs args)
    {
        if (string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase))
        {
            return await MetadataDocumentByteCache.GetOrBuildJsonAsync(
                context,
                new JsonMetadataCacheRequest<CollectionInfo>
                {
                    CacheKey = args.CacheKey,
                    TypeInfo = OgcJsonContext.Default.CollectionInfo,
                    BuildAsync = args.BuildAsync
                }).ConfigureAwait(false);
        }

        var collection = await args.BuildAsync().ConfigureAwait(false);
        return OgcCommonUtilities.FormatMetadataResponse(
            collection,
            OgcJsonContext.Default.CollectionInfo,
            outputFormat,
            collection.Title ?? collection.Id);
    }

    internal static Collections FinalizeCollectionsResponse(
        CollectionsBuildArgs args,
        ImmutableArray<CollectionInfo> collections)
    {
        var links = OgcCommonUtilities.BuildFormatLinks(
                args.Request,
                $"{args.BaseUrl}/ogc/features/collections",
                args.OutputFormat,
                OgcCommonUtilities.MetadataFormats,
                "Collections")
            .ToBuilder();

        links.Add(Link.Create(
            href: $"{args.BaseUrl}/ogc/features",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Landing page"));

        return new Collections
        {
            CollectionList = collections,
            Links = links.ToImmutable()
        };
    }

    internal static CollectionInfo FinalizeSingleCollectionResponse(
        SingleCollectionBuildArgs args,
        CollectionInfo built)
    {
        var collectionSegment = Uri.EscapeDataString(args.CollectionId);
        var basePath = $"{args.BaseUrl}/ogc/features/collections/{collectionSegment}";
        var selfHref = $"{basePath}{args.Request.QueryString}";
        var updatedLinks = built.Links.Select(link =>
                string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                    ? link with { Href = selfHref, Type = args.OutputFormat }
                    : link)
            .ToImmutableArray();

        updatedLinks = OgcCommonUtilities.AddAlternateLinks(
            updatedLinks, args.Request, basePath, args.OutputFormat, OgcCommonUtilities.MetadataFormats);
        return built with { Links = updatedLinks };
    }
}
