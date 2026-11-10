// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

// Every path ends in a wildcard so that all query-string variants of an address are invalidated with it.
internal static class CdnPaths
{
    internal static readonly string[] Everything = ["/*"];

    private static readonly string[] OgcCollectionFamilies = ["features", "tiles", "maps", "coverages", "styles"];

    internal static IEnumerable<string> ForService(string serviceId)
    {
        var service = Uri.EscapeDataString(serviceId);
        return
        [
            "/rest/services",
            "/rest/services?*",
            $"/rest/services/{service}/*",
            $"/ogc/services/{service}/*",
            $"/static/{service}/*",
        ];
    }

    internal static IEnumerable<string> ForLayer(int layerId)
        => [$"/tiles/{layerId}/*", $"/api/styles/{layerId}.json*"];

    internal static IEnumerable<string> ForCollection(string collectionId)
    {
        var collection = Uri.EscapeDataString(collectionId);
        return OgcCollectionFamilies.SelectMany(family => new[]
        {
            $"/ogc/{family}/collections",
            $"/ogc/{family}/collections?*",
            $"/ogc/{family}/collections/{collection}*",
        }).Append("/ogc/tiles/tiles/*");
    }
}
