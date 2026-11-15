// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

// The provider prefix plus the publish prefix: the only key space the range proxy serves.
internal static class PMTilesPublishKeyRoot
{
    public static string Resolve(CloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var providerPrefix = options.Provider switch
        {
            CloudStorageProvider.AwsS3 => options.AwsS3?.KeyPrefix,
            CloudStorageProvider.AzureBlob => options.AzureBlob?.BlobPrefix,
            _ => null
        };

        return string.Join('/', new[] { providerPrefix, options.PMTilesPublish?.KeyPrefix }
            .Select(Normalize)
            .Where(segment => segment.Length > 0));
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('/');
}
