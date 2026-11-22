// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.Ogc.Api.Features;

internal readonly record struct OgcCollectionCacheEntry(
    MetadataV2Layer Publication,
    MetadataV2Service? Service,
    MetadataV2Object Resource);

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
}
