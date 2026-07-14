// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit tests for <see cref="ImageServerSensorModel"/>, which parses the JSON sensor payloads
/// (off-nadir orientation and RPC) carried by <see cref="RasterSensorMetadata"/>.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerSensorModelTests
{
    [UnitTest]
    public void TryReadOffNadirAngle_WithCamelCaseField_ReturnsAngle()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """{"offNadirAngle": 12.5}""",
        };

        ImageServerSensorModel.TryReadOffNadirAngle(metadata).Should().Be(12.5);
    }

    [UnitTest]
    public void TryReadOffNadirAngle_WithSnakeCaseField_NormalisesSign()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """{"off_nadir_angle": -7}""",
        };

        ImageServerSensorModel.TryReadOffNadirAngle(metadata).Should().Be(7);
    }

    [UnitTest]
    public void TryReadOffNadirAngle_WithNoMetadata_ReturnsNull()
    {
        ImageServerSensorModel.TryReadOffNadirAngle(null).Should().BeNull();
    }

    [UnitTest]
    public void TryReadOffNadirAngle_WithMalformedJson_ReturnsNull()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = "{not-json",
        };

        ImageServerSensorModel.TryReadOffNadirAngle(metadata).Should().BeNull();
    }

    [UnitTest]
    public void TryReadRpc_WithOffsetScaleTerms_BuildsRoundTrippableModel()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            RpcJson = """
            {
                "sampleOffset": 1000, "lineOffset": 800,
                "longOffset": -120.0, "latOffset": 35.0,
                "sampleScale": 1000, "lineScale": 800,
                "longScale": 0.05, "latScale": 0.04
            }
            """,
        };

        var rpc = ImageServerSensorModel.TryReadRpc(metadata);
        rpc.Should().NotBeNull();
        var rpcValue = rpc!.Value;

        // The sample/line centre maps to the ground centre (offsets), and round-trips.
        var (lon, lat) = rpcValue.ImageToGround(1000, 800);
        lon.Should().BeApproximately(-120.0, 1e-9);
        lat.Should().BeApproximately(35.0, 1e-9);

        var (sample, line) = rpcValue.GroundToImage(lon, lat);
        sample.Should().BeApproximately(1000, 1e-6);
        line.Should().BeApproximately(800, 1e-6);

        // A one-scale offset in sample maps to a one-longitude-scale offset in ground.
        var (lon2, _) = rpcValue.ImageToGround(2000, 800);
        lon2.Should().BeApproximately(-120.0 + 0.05, 1e-9);
    }

    [UnitTest]
    public void TryReadRpc_WithZeroScale_ReturnsNullToAvoidDegenerateTransform()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            RpcJson = """
            {
                "sampleOffset": 0, "lineOffset": 0, "longOffset": 0, "latOffset": 0,
                "sampleScale": 0, "lineScale": 1, "longScale": 1, "latScale": 1
            }
            """,
        };

        ImageServerSensorModel.TryReadRpc(metadata).Should().BeNull();
    }

    [UnitTest]
    public void TryReadRpc_WithNoMetadata_ReturnsNull()
    {
        ImageServerSensorModel.TryReadRpc(null).Should().BeNull();
        ImageServerSensorModel.TryReadRpc(new RasterSensorMetadata { RasterDataId = 1 }).Should().BeNull();
    }

    [UnitTest]
    public void ReadControlPoints_WithPairedPoints_ParsesImageAndReferenceCoordinates()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """
            {
              "controlPoints": [
                { "imagePoint": { "x": 512.0, "y": 384.0 },
                  "referencePoint": { "x": -117.161, "y": 32.716, "z": 104.2, "spatialReference": { "wkid": 4326 } } },
                { "sourcePoint": { "x": 1024.0, "y": 768.0 },
                  "targetPoint": { "x": -117.155, "y": 32.720 } }
              ]
            }
            """,
        };

        var points = ImageServerSensorModel.ReadControlPoints(metadata, defaultReferenceSrid: 3857);

        points.Should().HaveCount(2);

        points[0].ImageX.Should().Be(512.0);
        points[0].ImageY.Should().Be(384.0);
        points[0].ReferenceX.Should().Be(-117.161);
        points[0].ReferenceY.Should().Be(32.716);
        points[0].ReferenceZ.Should().Be(104.2);
        points[0].ReferenceSrid.Should().Be(4326);

        // sourcePoint/targetPoint aliases parse; missing SR falls back to the default (raster) SRID.
        points[1].ImageX.Should().Be(1024.0);
        points[1].ReferenceZ.Should().BeNull();
        points[1].ReferenceSrid.Should().Be(3857);
    }

    [UnitTest]
    public void ReadControlPoints_WithTiePointsAndGcpsAliases_ParsesArray()
    {
        var tiePoints = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """
            { "tiePoints": [ { "imagePoint": { "x": 1, "y": 2 }, "referencePoint": { "x": 3, "y": 4 } } ] }
            """,
        };
        var gcps = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """
            { "gcps": [ { "imagePoint": { "x": 1, "y": 2 }, "referencePoint": { "x": 3, "y": 4 } } ] }
            """,
        };

        ImageServerSensorModel.ReadControlPoints(tiePoints).Should().HaveCount(1);
        ImageServerSensorModel.ReadControlPoints(gcps).Should().HaveCount(1);
    }

    [UnitTest]
    public void ReadControlPoints_SkipsEntriesMissingEitherPointOrCoordinates()
    {
        var metadata = new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """
            {
              "controlPoints": [
                { "imagePoint": { "x": 1, "y": 2 } },
                { "referencePoint": { "x": 3, "y": 4 } },
                { "imagePoint": { "x": 5 }, "referencePoint": { "x": 6, "y": 7 } },
                { "imagePoint": { "x": 8, "y": 9 }, "referencePoint": { "x": 10, "y": 11 } }
              ]
            }
            """,
        };

        var points = ImageServerSensorModel.ReadControlPoints(metadata);
        points.Should().HaveCount(1);
        points[0].ImageX.Should().Be(8);
    }

    [UnitTest]
    public void ReadControlPoints_WithNoMetadataOrControlPoints_ReturnsEmpty()
    {
        ImageServerSensorModel.ReadControlPoints(null).Should().BeEmpty();
        ImageServerSensorModel.ReadControlPoints(new RasterSensorMetadata { RasterDataId = 1 }).Should().BeEmpty();
        ImageServerSensorModel.ReadControlPoints(new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = """{"offNadirAngle": 5}""",
        }).Should().BeEmpty();
        ImageServerSensorModel.ReadControlPoints(new RasterSensorMetadata
        {
            RasterDataId = 1,
            ExteriorOrientationJson = "{not-json",
        }).Should().BeEmpty();
    }
}
