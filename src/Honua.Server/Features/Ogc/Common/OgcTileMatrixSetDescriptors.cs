// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Server.Features.Ogc.Common;

internal sealed record OgcTileMatrixSetDescriptor(
    string Id,
    string Crs,
    string Uri,
    string ProjectionName);

internal static class OgcTileMatrixSetDescriptors
{
    private static readonly ImmutableArray<OgcTileMatrixSetDescriptor> _supported =
    [
        new(
            "WebMercatorQuad",
            "http://www.opengis.net/def/crs/EPSG/0/3857",
            "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad",
            "Web Mercator"),
        new(
            "WorldCRS84Quad",
            "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
            "http://www.opengis.net/def/tilematrixset/OGC/1.0/WorldCRS84Quad",
            "WGS84")
    ];

    public static ImmutableArray<OgcTileMatrixSetDescriptor> Supported => _supported;

    public static bool TryGet(string tileMatrixSetId, out OgcTileMatrixSetDescriptor descriptor)
    {
        for (var i = 0; i < _supported.Length; i++)
        {
            var candidate = _supported[i];
            if (string.Equals(candidate.Id, tileMatrixSetId, StringComparison.OrdinalIgnoreCase))
            {
                descriptor = candidate;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }
}
