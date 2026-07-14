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
}
