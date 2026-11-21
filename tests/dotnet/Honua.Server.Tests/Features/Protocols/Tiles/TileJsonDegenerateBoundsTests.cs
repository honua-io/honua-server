// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

[Collection("Database")]
public sealed class TileJsonDegenerateBoundsTests : IAsyncLifetime
{
    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader>();
    private readonly WebAppFixture _fixture;

    public TileJsonDegenerateBoundsTests()
    {
        _fixture = new WebAppFixture().ReplaceService(_reader);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("-122.4194 37.78 -122.4194 37.78")]
    [InlineData("-122.4194 37.70 -122.4194 37.80")]
    [InlineData("-122.50 37.78 -122.30 37.78")]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task GetTileJson_PointOrAxisLine_AdvertisesOneTileWideBounds(string sourceExtent)
    {
        var source = sourceExtent.Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var (west, south, east, north) = (source[0], source[1], source[2], source[3]);
        _reader.GetExtentAsync(Arg.Any<int>(), Arg.Any<FeatureQuery?>(), Arg.Any<CancellationToken>())
            .Returns(FeatureExtent.Create(west, south, east, north, SpatialReference.WGS84.Wkid));

        using var response = await _fixture.Client.GetAsync($"/tiles/{WebAppFixture.TestLayerId}/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bounds = json.RootElement.GetProperty("bounds").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var tileSpan = 360.0 / Math.Pow(2, json.RootElement.GetProperty("maxzoom").GetInt32());
        bounds[0].Should().BeLessThanOrEqualTo(west);
        bounds[1].Should().BeLessThanOrEqualTo(south);
        bounds[2].Should().BeGreaterThanOrEqualTo(east);
        bounds[3].Should().BeGreaterThanOrEqualTo(north);
        (bounds[2] - bounds[0]).Should().BeApproximately(east > west ? east - west : tileSpan, 1e-9);
        (bounds[3] - bounds[1]).Should().BeApproximately(north > south ? north - south : tileSpan, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task GetTileJson_DatelineCrossingExtent_AdvertisesFullLongitudeRange()
    {
        _reader.GetExtentAsync(Arg.Any<int>(), Arg.Any<FeatureQuery?>(), Arg.Any<CancellationToken>())
            .Returns(FeatureExtent.Create(177.0, -19.0, -178.0, -16.0, SpatialReference.WGS84.Wkid));

        using var response = await _fixture.Client.GetAsync($"/tiles/{WebAppFixture.TestLayerId}/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("bounds").EnumerateArray().Select(value => value.GetDouble())
            .Should().Equal(-180.0, -19.0, 180.0, -16.0);
        json.RootElement.GetProperty("center")[0].GetDouble().Should().Be(0);
    }
}
