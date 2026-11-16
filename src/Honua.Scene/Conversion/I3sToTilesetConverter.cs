// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Features.Scene.Conversion;

public static class I3sToTilesetConverter
{
    public const int Wgs84Wkid = 4326;

    private const string GeneratorTag = "honua-i3s-converter";

    public static IReadOnlySet<string> SupportedLayerTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "3DObject", "IntegratedMesh" };

    public static TilesetDocument Convert(I3sSceneLayerDocument layer, string? rootContentUri = null)
    {
        ArgumentNullException.ThrowIfNull(layer);

        var layerType = layer.LayerType ?? string.Empty;
        if (!SupportedLayerTypes.Contains(layerType))
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.UnsupportedLayerType,
                $"I3S layer type '{(string.IsNullOrEmpty(layerType) ? "(unspecified)" : layerType)}' is not supported by the conversion slice. Supported: 3DObject, IntegratedMesh.");
        }

        if (layer.FullExtent is not { } extent)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.MissingExtent,
                "The I3S scene layer does not declare a fullExtent and cannot be placed geographically.");
        }

        var wkid = extent.SpatialReference?.Wkid
            ?? extent.SpatialReference?.LatestWkid
            ?? layer.SpatialReference?.Wkid
            ?? layer.SpatialReference?.LatestWkid
            ?? 0;

        if (wkid != Wgs84Wkid)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.UnsupportedSpatialReference,
                $"The I3S scene layer uses spatial reference WKID {wkid}; the conversion slice only places WGS-84 (4326) geographic layers.");
        }

        if (extent.Xmin > extent.Xmax || extent.Ymin > extent.Ymax)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.MissingExtent,
                "The I3S fullExtent is degenerate (min bound exceeds max bound).");
        }

        // The extent is consumed below as WGS-84 degrees (boundingRegionDegrees);
        // validate it lies within the geographic ranges (lon [-180,180], lat
        // [-90,90]) before treating it as such. A WKID-4326 layer with bounds in
        // a projected unit (e.g. metres) would otherwise produce a bogus region
        // far outside the globe.
        if (!double.IsFinite(extent.Xmin) || !double.IsFinite(extent.Ymin)
            || !double.IsFinite(extent.Xmax) || !double.IsFinite(extent.Ymax)
            || extent.Xmin < -180.0 || extent.Xmax > 180.0
            || extent.Ymin < -90.0 || extent.Ymax > 90.0)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.MissingExtent,
                "The I3S fullExtent is outside the WGS-84 geographic range (longitude [-180,180], latitude [-90,90]).");
        }

        var minHeight = extent.Zmin ?? 0.0;
        var maxHeight = extent.Zmax ?? 0.0;

        // Geometric error sized to the 3D geographic diagonal of the extent so a
        // 3D Tiles client refines at a sensible scale, via the shared geodesy
        // helper used by every scene builder (GeodesicError.RootGeometricError):
        // a cos(midLatitude)-corrected longitude span, the meridian-arc latitude
        // span, AND the vertical (Zmin/Zmax) extent in the diagonal — the prior
        // I3S-local copy dropped the height term, understating the error for tall,
        // geographically small layers (a single skyscraper or a cliff mesh).
        // Determinism is preserved (pure arithmetic on the extent bounds).
        var geometricError = GeodesicError.RootGeometricError(
            extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax, minHeight, maxHeight);

        var contentUris = string.IsNullOrWhiteSpace(rootContentUri)
            ? Array.Empty<string>()
            : new[] { rootContentUri };

        return TilesetDocumentWriter.Build(
            boundingRegionDegrees: [extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax],
            minHeightMeters: minHeight,
            maxHeightMeters: maxHeight,
            geometricError: geometricError,
            tileContentUris: contentUris,
            generatorTag: GeneratorTag);
    }

}
