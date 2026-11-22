// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Api.Coverages.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Coverages.Services;

// Collection metadata GDAL's OGC API driver needs to open a Zarr coverage as GeoTIFF.
internal sealed partial class ZarrCoverageService
{
    private static void AddGeoTiffCoverageLink(ImmutableArray<Link>.Builder links, string basePath, CoverageDomainSet? domainSet)
    {
        if (domainSet is not null)
        {
            links.Add(Link.Create(
                href: $"{basePath}/coverage?f=geotiff",
                rel: RelationTypes.Coverage,
                type: GeoTiffLinkType,
                title: "Coverage data as GeoTIFF"));
        }
    }

    // GDAL's OGC API driver sizes the raster and names the trim axes from the domainset and takes
    // the band count and data type from the range type; both describe the default GeoTIFF band.
    private static CoverageDomainSet? CreateGeoTiffDomainSet(ZarrStoreMetadata metadata, ZarrArrayMetadata primary)
    {
        var xIndex = Array.FindIndex(primary.DimensionNames, name => IsXDimension(metadata, name));
        var yIndex = Array.FindIndex(primary.DimensionNames, name => IsYDimension(metadata, name));
        var extent = metadata.Extent;
        if (!ZarrGeoTiffRenderer.SupportsDataType(primary.DataType) || xIndex < 0 || yIndex < 0 || primary.Shape[xIndex] <= 0 || primary.Shape[yIndex] <= 0 ||
            extent.XMax <= extent.XMin || extent.YMax <= extent.YMin)
        {
            return null;
        }

        var geographic = metadata.Srid == 4326;
        if (!geographic && GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(metadata.Srid))
        {
            // GDAL swaps the trim axes of lat/long-ordered EPSG CRSs; only CRS84 order is served.
            return null;
        }

        return new CoverageDomainSet
        {
            GeneralGrid = new CoverageGeneralGrid
            {
                SrsName = geographic ? OgcFeaturesUtilities.Crs84Uri : CreateEpsgUri(metadata.Srid),
                AxisLabels = geographic ? ["Lon", "Lat"] : ["x", "y"],
                Axes =
                [
                    new CoverageGridAxis { LowerBound = extent.XMin, UpperBound = extent.XMax, Resolution = (extent.XMax - extent.XMin) / primary.Shape[xIndex] },
                    new CoverageGridAxis { LowerBound = extent.YMin, UpperBound = extent.YMax, Resolution = (extent.YMax - extent.YMin) / primary.Shape[yIndex] }
                ]
            }
        };
    }

    private static CoverageRangeType CreateGeoTiffRangeType(ZarrArrayMetadata primary)
    {
        // GDAL's OGC API driver knows no INT8 or 64-bit integer names: widen those to types it maps.
        var definition = ZarrTileRenderer.Normalize(primary.DataType) switch
        {
            "u1" or "b1" => "UINT8",
            "i1" or "i2" => "INT16",
            "u2" => "UINT16",
            "i4" => "INT32",
            "u4" => "UINT32",
            "f4" => "FLOAT32",
            _ => "FLOAT64"
        };
        return new CoverageRangeType { Fields = [new CoverageRangeField { Name = primary.Name, Definition = definition }] };
    }
}
