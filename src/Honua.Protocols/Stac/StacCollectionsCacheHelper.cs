// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Stac.Services;

namespace Honua.Protocols.Stac;

internal static class StacCollectionsCacheHelper
{
    internal static string BuildCollectionsKey(
        string baseUrl,
        ReadOnlySpan<StacV2Lookups.ResolvedStacPublication> visible)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("stac-collections|")
                .Append(visible.Length > 0 ? visible[0].Snapshot.Etag : "empty")
                .Append('|').Append(baseUrl);

            foreach (var entry in visible)
            {
                sb.Append('|').Append(entry.Publication.Metadata.Id)
                    .Append(':').Append(entry.Publication.Metadata.Generation.GetValueOrDefault())
                    .Append(':').Append(entry.Service.Metadata.Id)
                    .Append(':').Append(entry.Service.Metadata.Generation.GetValueOrDefault())
                    .Append(':').Append(entry.Resource.Metadata.Id)
                    .Append(':').Append(entry.Resource.Metadata.Generation.GetValueOrDefault());
            }

            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }

    internal static string BuildCollectionKey(
        string baseUrl,
        StacV2Lookups.ResolvedStacPublication entry)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("stac-collection|").Append(entry.Snapshot.Etag)
                .Append('|').Append(baseUrl)
                .Append('|').Append(entry.Publication.Metadata.Id)
                .Append(':').Append(entry.Publication.Metadata.Generation.GetValueOrDefault())
                .Append(':').Append(entry.Service.Metadata.Id)
                .Append(':').Append(entry.Service.Metadata.Generation.GetValueOrDefault())
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
