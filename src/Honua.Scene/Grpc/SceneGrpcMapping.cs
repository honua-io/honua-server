// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Scene.Domain;
using Proto = Geospatial.V1;

namespace Honua.Scene.Grpc;

/// <summary>
/// Pure mapping helpers between the server's scene/elevation domain types and
/// the canonical <c>geospatial.v1</c> gRPC messages. Kept free of I/O so the
/// projection can be unit-tested in isolation from the service hosts.
/// </summary>
internal static class SceneGrpcMapping
{
    internal const string ThreeDimensionalTilesCapability = "3d-tiles";
    private const int Wgs84 = 4326;

    /// <summary>Meters per degree of latitude, used to derive a default camera standoff.</summary>
    private const double MetersPerDegree = 111_320d;

    /// <summary>
    /// Projects a registered (database-backed) scene record into the gRPC
    /// <see cref="Proto.SceneMetadata"/> contract.
    /// </summary>
    public static Proto.SceneMetadata ToSceneMetadata(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var metadata = new Proto.SceneMetadata
        {
            SceneId = record.Id,
            Title = record.Name,
            Description = record.Description ?? string.Empty,
            TilesetUrl = BuildTilesetPath(record.Id),
            TerrainUrl = string.Empty,
            Edition = record.EditionGate ?? string.Empty,
        };

        metadata.Capabilities.Add(ThreeDimensionalTilesCapability);

        if (record.Extent is { } extent)
        {
            metadata.Extent = ToExtent3D(extent);
            metadata.InitialCamera = ToInitialCamera(extent);
        }

        return metadata;
    }

    /// <summary>
    /// Projects a configuration-backed scene (no persisted extent) into the
    /// gRPC <see cref="Proto.SceneMetadata"/> contract.
    /// </summary>
    public static Proto.SceneMetadata ToSceneMetadata(SceneDataset scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var metadata = new Proto.SceneMetadata
        {
            SceneId = scene.Id,
            Title = scene.Name,
            Description = scene.Description ?? string.Empty,
            TilesetUrl = BuildTilesetPath(scene.Id),
            TerrainUrl = string.Empty,
            Edition = string.Empty,
        };

        metadata.Capabilities.Add(ThreeDimensionalTilesCapability);
        return metadata;
    }

    /// <summary>
    /// Maps a 2D scene extent to the 3D gRPC envelope. Height bounds are left
    /// at zero because the persisted scene extent is horizontal-only; callers
    /// that need true vertical bounds read them from the tileset bounding
    /// volume served by the gRPC <c>TileService</c>.
    /// </summary>
    public static Proto.Extent3D ToExtent3D(SceneExtent extent)
    {
        ArgumentNullException.ThrowIfNull(extent);

        return new Proto.Extent3D
        {
            Extent = new Proto.Extent
            {
                Xmin = extent.XMin,
                Ymin = extent.YMin,
                Xmax = extent.XMax,
                Ymax = extent.YMax,
                SpatialReference = new Proto.SpatialReference { Wkid = Wgs84, LatestWkid = Wgs84 },
            },
            MinHeight = 0,
            MaxHeight = 0,
        };
    }

    /// <summary>
    /// Derives a suggested initial camera centered over the extent, standing
    /// off at a height scaled to the larger horizontal span so the whole scene
    /// is comfortably framed.
    /// </summary>
    public static Proto.Camera ToInitialCamera(SceneExtent extent)
    {
        ArgumentNullException.ThrowIfNull(extent);

        var centerLon = (extent.XMin + extent.XMax) / 2d;
        var centerLat = (extent.YMin + extent.YMax) / 2d;
        var spanDegrees = Math.Max(
            Math.Abs(extent.XMax - extent.XMin),
            Math.Abs(extent.YMax - extent.YMin));
        var height = Math.Max(1_000d, spanDegrees * MetersPerDegree * 1.5d);

        return new Proto.Camera
        {
            Longitude = centerLon,
            Latitude = centerLat,
            Height = height,
            Heading = 0,
            Pitch = -45,
            Roll = 0,
        };
    }

    /// <summary>Formats a raster merge strategy back to its canonical rule string.</summary>
    public static string FormatMergeStrategy(RasterMergeStrategy strategy) => strategy switch
    {
        RasterMergeStrategy.Oldest => "oldest",
        RasterMergeStrategy.Average => "average",
        RasterMergeStrategy.Max => "max",
        RasterMergeStrategy.Min => "min",
        _ => "newest",
    };

    /// <summary>Maps a point elevation result into the gRPC response.</summary>
    public static Proto.GetElevationResponse ToElevationResponse(
        ElevationPointResult result,
        RasterMergeStrategy mergeStrategy)
    {
        ArgumentNullException.ThrowIfNull(result);

        var response = new Proto.GetElevationResponse
        {
            NoData = result.NoData,
            OutOfBounds = result.OutOfBounds,
            X = result.X,
            Y = result.Y,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            Source = ToElevationSource(result.RasterIds, result.SourceSrid, result.PixelType, result.NoDataValue, result.VerticalUnit, result.VerticalDatum),
        };

        if (result is { Elevation: { } elevation, NoData: false, OutOfBounds: false })
        {
            response.Elevation = elevation;
        }

        return response;
    }

    /// <summary>Maps a profile elevation result into the gRPC response.</summary>
    public static Proto.GetElevationProfileResponse ToElevationProfileResponse(
        ElevationProfileResult result,
        RasterMergeStrategy mergeStrategy)
    {
        ArgumentNullException.ThrowIfNull(result);

        var response = new Proto.GetElevationProfileResponse
        {
            LineLengthMeters = result.LineLengthMeters,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            IsAllNoData = result.IsAllNoData,
            Source = ToElevationSource(result.RasterIds, result.SourceSrid, result.PixelType, result.NoDataValue, result.VerticalUnit, result.VerticalDatum),
        };

        foreach (var sample in result.Samples)
        {
            var profileSample = new Proto.ElevationProfileSample
            {
                DistanceMeters = sample.DistanceMeters,
                NoData = sample.NoData,
            };

            if (sample is { Elevation: { } elevation, NoData: false })
            {
                profileSample.Elevation = elevation;
            }

            response.Samples.Add(profileSample);
        }

        return response;
    }

    private static Proto.ElevationSource ToElevationSource(
        long[] rasterIds,
        int? sourceSrid,
        string? pixelType,
        double? noDataValue,
        string? verticalUnit,
        string? verticalDatum)
    {
        var source = new Proto.ElevationSource
        {
            PixelType = pixelType ?? string.Empty,
            VerticalUnit = verticalUnit ?? string.Empty,
            VerticalDatum = verticalDatum ?? string.Empty,
        };

        source.RasterIds.AddRange(rasterIds);

        if (sourceSrid is { } srid)
        {
            source.SourceSpatialReference = new Proto.SpatialReference { Wkid = srid, LatestWkid = srid };
        }

        if (noDataValue is { } noData)
        {
            source.NoDataValue = noData;
        }

        return source;
    }

    private static string BuildTilesetPath(string sceneId)
        => string.Create(CultureInfo.InvariantCulture, $"/scenes/{sceneId}/tileset.json");
}
