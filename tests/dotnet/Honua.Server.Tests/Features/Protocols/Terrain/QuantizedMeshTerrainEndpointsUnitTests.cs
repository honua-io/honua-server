// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Terrain;
using Honua.Server.Features.Protocols.Terrain.QuantizedMesh;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

public sealed class QuantizedMeshTerrainEndpointsUnitTests
{
    [UnitTheory]
    [InlineData("application/vnd.quantized-mesh;extensions=octvertexnormals", true)]
    [InlineData("application/vnd.quantized-mesh;extensions=octvertexnormals-watermask,application/octet-stream;q=0.9", true)]
    [InlineData("application/vnd.quantized-mesh; extensions=\"octvertexnormals\"", true)]
    [InlineData("application/vnd.quantized-mesh", false)]
    [InlineData("application/octet-stream", false)]
    public void RequestsOctVertexNormals_AcceptHeader_ParsesExtensionParameter(string acceptHeader, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = acceptHeader;

        QuantizedMeshTerrainEndpoints.RequestsOctVertexNormals(context.Request).Should().Be(expected);
    }

    [UnitTest]
    public void RequestsOctVertexNormals_QueryParameter_ParsesExtensionToken()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?extensions=octvertexnormals");

        QuantizedMeshTerrainEndpoints.RequestsOctVertexNormals(context.Request).Should().BeTrue();
    }

    [UnitTest]
    public void BuildCapabilities_AndLinks_AdvertiseBothFormatsAndHonestUnavailableStatus()
    {
        var supported = new TerrainDatasetMetadata
        {
            LayerId = 7,
            RasterIds = [101],
            RasterCount = 1,
            SourceSrid = 4326,
            Wgs84Bounds = [-156.7, 20.5, -155.9, 21.1],
            IsSupported = true
        };

        var caps = TerrainEndpoints.BuildCapabilities("https://county.example.gov", "county-dem", supported);
        caps.TerrainRgb.Available.Should().BeTrue();
        caps.TerrainRgb.Status.Should().Be("available");
        caps.TerrainRgb.Href.Should().Be("https://county.example.gov/terrain/county-dem/tile.json");
        caps.QuantizedMesh.Available.Should().BeTrue();
        caps.QuantizedMesh.Status.Should().Be("available");
        caps.QuantizedMesh.Href.Should().Be("https://county.example.gov/terrain/county-dem/layer.json");

        var links = TerrainEndpoints.BuildLinks("https://county.example.gov", "county-dem", selfIsQuantizedMesh: false);
        links.Select(static l => (l.Rel, l.Format, l.Href)).Should().Equal(
            ("self", "terrain-rgb", "https://county.example.gov/terrain/county-dem/tile.json"),
            ("alternate", "quantized-mesh-1.0", "https://county.example.gov/terrain/county-dem/layer.json"));

        var unsupported = supported with
        {
            IsSupported = false,
            UnsupportedReasons = ["Source raster CRS is unavailable."]
        };
        var unavailableCaps = TerrainEndpoints.BuildCapabilities("https://county.example.gov", "county-dem", unsupported);
        unavailableCaps.TerrainRgb.Available.Should().BeFalse();
        unavailableCaps.TerrainRgb.Status.Should().Be("unavailable");
        unavailableCaps.TerrainRgb.Reasons.Should().Contain("Source raster CRS is unavailable.");
        unavailableCaps.QuantizedMesh.Available.Should().BeFalse();
        unavailableCaps.QuantizedMesh.Status.Should().Be("unavailable");
        unavailableCaps.QuantizedMesh.Reasons.Should().Contain("Source raster CRS is unavailable.");
    }
}
