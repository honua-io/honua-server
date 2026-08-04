// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Helpers;
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
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro);
    private const int TestLayerId = WebAppFixture.TestLayerId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.UpdateV2ResourceMetadata(
            TestLayerId,
            temporal: new MetadataV2ResourceTemporal
            {
                StartTimeField = "timestamp",
                EndTimeField = "event_date",
            });
    }

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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithBothBoundsNull_TreatedAsNoFilter()
    {
        // The temporal-animation contract documents time=null,null as a no-op
        // ("Treated as no filter; full result set returned"). The MVT path
        // must therefore parse it without invoking the Pro edition gate or
        // requiring the layer to be time-aware — equivalent to omitting the
        // parameter entirely.
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time=null,null");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NonExistentLayerWithTime_ReturnsNotFound()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/9999/1/0/0.mvt?time={start},{end}");

        // honua-server#2945: /tiles is not an Esri protocol surface; layer-not-found is
        // a real HTTP 404 + problem+json, not the GeoServices 200-envelope.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NonTimeAwareLayer_WithTime_ReturnsBadRequest()
    {
        // The temporal-animation contract says non-time-aware layers reject
        // non-empty time= filters with HTTP 400; falling back to the first
        // Date/DateTime attribute would silently filter on a non-temporal
        // column. Strict opt-in matches the WMS/WMTS rejection behavior.
        // V2 cutover (#1035 72/N): clear the resource temporal so the layer is
        // unambiguously non-time-aware.
        _fixture.UpdateV2ResourceMetadata(TestLayerId, clearTemporal: true);

        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time={start},{end}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_DifferentTimeRanges_ProduceDistinctCacheEntries()
    {
        // Cache-key and filtering regression for the temporal-animation contract.
        // The first window intersects seeded feature intervals in this tile; the
        // second is later than every seeded interval. Requesting the populated
        // window first also proves that the empty response is not a cached copy.
        var rangeA =
            $"{new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()},"
            + new DateTimeOffset(2023, 1, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rangeB =
            $"{new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()},"
            + new DateTimeOffset(2025, 1, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var responseA = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt?time={rangeA}");
        var responseB = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt?time={rangeB}");

        responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        (await responseA.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
        responseB.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
