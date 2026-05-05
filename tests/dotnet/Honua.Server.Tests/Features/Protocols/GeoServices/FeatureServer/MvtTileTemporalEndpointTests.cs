// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for MVT vector tile temporal filtering introduced for ticket #379.
/// The default test host runs as Pro edition so the time-series tile gate passes;
/// the gate logic itself is unit-tested separately. Covers ?time= acceptance,
/// validation of malformed values, and non-regression for tiles requested without
/// the parameter.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.GetTile)]
public sealed class MvtTileTemporalEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = WebAppFixture.TestLayerId;

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NoTimeParameter_ReturnsTile()
    {
        // Non-regression: tiles requested without ?time= must be unaffected.
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithTimeRangeUnixMs_ReturnsTile()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time={start},{end}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithTimeRangeIso8601_ReturnsTile()
    {
        var range = "2024-01-01T00:00:00Z,2024-12-31T23:59:59Z";
        var encoded = Uri.EscapeDataString(range);

        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time={encoded}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithTimeOpenStart_ReturnsTile()
    {
        var end = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time=null,{end}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithMalformedTime_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time=not-a-timestamp");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithReversedTimeRange_ReturnsBadRequest()
    {
        // Start > End is rejected by GeoServicesTemporalQueryBuilder.TryParseTimeParameter
        var start = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time={start},{end}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NonExistentLayerWithTime_ReturnsNotFound()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/9999/1/0/0.mvt?time={start},{end}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
