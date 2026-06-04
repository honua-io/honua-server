// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Raster;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Unit tests for the pure scene/elevation gRPC projection helpers.
/// </summary>
public sealed class SceneGrpcMappingTests
{
    [UnitTest]
    public void ToSceneMetadata_FromRecord_MapsCoreFieldsAndTilesetUrl()
    {
        var record = new SceneDatasetRecord
        {
            Id = "downtown",
            Name = "Downtown",
            Description = "A scene",
            AssetRoot = "/data/downtown",
            CreatedBy = "tester",
            Extent = new SceneExtent(-158.0, 21.0, -157.0, 22.0),
        };

        var metadata = SceneGrpcMapping.ToSceneMetadata(record);

        metadata.SceneId.Should().Be("downtown");
        metadata.Title.Should().Be("Downtown");
        metadata.Description.Should().Be("A scene");
        metadata.TilesetUrl.Should().Be("/scenes/downtown/tileset.json");
        metadata.Capabilities.Should().Contain("3d-tiles");
        metadata.Extent.Should().NotBeNull();
        metadata.InitialCamera.Should().NotBeNull();
    }

    [UnitTest]
    public void ToExtent3D_MapsEnvelopeAndDefaultsToWgs84()
    {
        var extent3d = SceneGrpcMapping.ToExtent3D(new SceneExtent(-10, -20, 30, 40));

        extent3d.Extent.Xmin.Should().Be(-10);
        extent3d.Extent.Ymin.Should().Be(-20);
        extent3d.Extent.Xmax.Should().Be(30);
        extent3d.Extent.Ymax.Should().Be(40);
        extent3d.Extent.SpatialReference.Wkid.Should().Be(4326);
    }

    [UnitTest]
    public void ToInitialCamera_CentersOverExtent()
    {
        var camera = SceneGrpcMapping.ToInitialCamera(new SceneExtent(-158, 21, -156, 23));

        camera.Longitude.Should().BeApproximately(-157, 1e-9);
        camera.Latitude.Should().BeApproximately(22, 1e-9);
        camera.Height.Should().BeGreaterThan(0);
        camera.Pitch.Should().Be(-45);
    }

    [UnitTest]
    public void FormatMergeStrategy_RoundTripsThroughSharedMosaicResolver()
    {
        // The elevation gRPC adapter now resolves the mosaic rule through the
        // shared RasterMosaicUtilities (#11) rather than a protocol-local parser.
        // FormatMergeStrategy must emit a canonical token that the shared resolver
        // round-trips back to the same strategy.
        foreach (var strategy in new[]
        {
            RasterMergeStrategy.Newest,
            RasterMergeStrategy.Oldest,
            RasterMergeStrategy.Average,
            RasterMergeStrategy.Max,
            RasterMergeStrategy.Min,
        })
        {
            var formatted = SceneGrpcMapping.FormatMergeStrategy(strategy);
            RasterMosaicUtilities.ResolveMergeStrategy(resource: null, formatted).Should().Be(strategy);
        }
    }

    [UnitTest]
    public void ToElevationResponse_WithValue_SetsElevationAndSource()
    {
        var result = new ElevationPointResult
        {
            LayerId = 7,
            Elevation = 123.5,
            NoData = false,
            OutOfBounds = false,
            X = -157.0,
            Y = 21.0,
            QuerySrid = 4326,
            RasterIds = new long[] { 11, 22 },
            SourceSrid = 3857,
            PixelType = "F32",
            NoDataValue = -9999.0,
            VerticalUnit = "meter",
            VerticalDatum = "EGM2008",
        };

        var response = SceneGrpcMapping.ToElevationResponse(result, RasterMergeStrategy.Max);

        response.HasElevation.Should().BeTrue();
        response.Elevation.Should().Be(123.5);
        response.NoData.Should().BeFalse();
        response.OutOfBounds.Should().BeFalse();
        response.X.Should().Be(-157.0);
        response.Y.Should().Be(21.0);
        response.MosaicRule.Should().Be("max");
        response.Source.Should().NotBeNull();
        response.Source.RasterIds.Should().Equal(11, 22);
        response.Source.PixelType.Should().Be("F32");
        response.Source.VerticalUnit.Should().Be("meter");
        response.Source.VerticalDatum.Should().Be("EGM2008");
        response.Source.SourceSpatialReference.Wkid.Should().Be(3857);
        response.Source.HasNoDataValue.Should().BeTrue();
        response.Source.NoDataValue.Should().Be(-9999.0);
    }

    [UnitTest]
    public void ToElevationResponse_NoData_LeavesElevationUnset()
    {
        var result = new ElevationPointResult
        {
            LayerId = 7,
            Elevation = 50.0, // present but suppressed because NoData is true
            NoData = true,
            OutOfBounds = false,
            X = 0,
            Y = 0,
            QuerySrid = 4326,
            RasterIds = Array.Empty<long>(),
        };

        var response = SceneGrpcMapping.ToElevationResponse(result, RasterMergeStrategy.Newest);

        response.HasElevation.Should().BeFalse(
            "the proto leaves elevation unset for a no-data sample even when a numeric value is present.");
        response.NoData.Should().BeTrue();
    }

    [UnitTest]
    public void ToElevationResponse_OutOfBounds_LeavesElevationUnset()
    {
        var result = new ElevationPointResult
        {
            LayerId = 7,
            Elevation = 50.0,
            NoData = false,
            OutOfBounds = true,
            X = 0,
            Y = 0,
            QuerySrid = 4326,
            RasterIds = Array.Empty<long>(),
        };

        var response = SceneGrpcMapping.ToElevationResponse(result, RasterMergeStrategy.Newest);

        response.HasElevation.Should().BeFalse();
        response.OutOfBounds.Should().BeTrue();
    }

    [UnitTest]
    public void ToElevationResponse_NoSourceSridOrNoDataValue_LeavesOptionalsUnset()
    {
        var result = new ElevationPointResult
        {
            LayerId = 7,
            Elevation = 1.0,
            NoData = false,
            OutOfBounds = false,
            X = 0,
            Y = 0,
            QuerySrid = 4326,
            RasterIds = Array.Empty<long>(),
            SourceSrid = null,
            NoDataValue = null,
        };

        var response = SceneGrpcMapping.ToElevationResponse(result, RasterMergeStrategy.Newest);

        response.Source.SourceSpatialReference.Should().BeNull(
            "a null source SRID must not populate the optional spatial reference.");
        response.Source.HasNoDataValue.Should().BeFalse();
        response.Source.PixelType.Should().BeEmpty();
    }

    [UnitTest]
    public void ToElevationProfileResponse_MapsSamplesAndSuppressesNoDataElevation()
    {
        var result = new ElevationProfileResult
        {
            LayerId = 7,
            SampleCount = 3,
            LineLengthMeters = 1000.0,
            IsAllNoData = false,
            Samples = new[]
            {
                new ElevationSample { DistanceMeters = 0, Elevation = 10.0, NoData = false },
                new ElevationSample { DistanceMeters = 500, Elevation = 20.0, NoData = true },
                new ElevationSample { DistanceMeters = 1000, Elevation = 30.0, NoData = false },
            },
            RasterIds = new long[] { 5 },
            SourceSrid = 4326,
            PixelType = "F32",
            NoDataValue = -9999.0,
            VerticalUnit = "meter",
            VerticalDatum = "EGM96",
        };

        var response = SceneGrpcMapping.ToElevationProfileResponse(result, RasterMergeStrategy.Min);

        response.LineLengthMeters.Should().Be(1000.0);
        response.IsAllNoData.Should().BeFalse();
        response.MosaicRule.Should().Be("min");
        response.Samples.Should().HaveCount(3);

        response.Samples[0].HasElevation.Should().BeTrue();
        response.Samples[0].Elevation.Should().Be(10.0);
        response.Samples[0].DistanceMeters.Should().Be(0);

        response.Samples[1].HasElevation.Should().BeFalse(
            "a no-data profile sample leaves its elevation unset even when a numeric value is present.");
        response.Samples[1].NoData.Should().BeTrue();

        response.Samples[2].HasElevation.Should().BeTrue();
        response.Samples[2].Elevation.Should().Be(30.0);

        response.Source.RasterIds.Should().Equal(5);
        response.Source.SourceSpatialReference.Wkid.Should().Be(4326);
        response.Source.VerticalDatum.Should().Be("EGM96");
    }
}
