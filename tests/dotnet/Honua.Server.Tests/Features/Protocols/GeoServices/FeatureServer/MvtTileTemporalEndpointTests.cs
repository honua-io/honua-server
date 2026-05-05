// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
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

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NonTimeAwareLayer_WithTime_ReturnsBadRequest()
    {
        // The temporal-animation contract says non-time-aware layers reject
        // non-empty time= filters with HTTP 400; falling back to the first
        // Date/DateTime attribute would silently filter on a non-temporal
        // column. Strict opt-in matches the WMS/WMTS rejection behavior.
        var updater = _fixture.GetService<ILayerMetadataUpdater>();
        await updater.UpdateLayerMetadataAsync(TestLayerId, new CatalogMetadata());

        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var response = await _fixture.Client.GetAsync(
            $"/tiles/{TestLayerId}/1/0/0.mvt?time={start},{end}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_DifferentTimeRanges_ProduceDistinctCacheEntries()
    {
        // Cache-key regression for the temporal-animation contract: the MvtTile
        // output-cache policy must vary by `time` so two different ranges
        // cannot collide on the same cached body. Both windows cover seeded
        // rows so the responses are valid; we only need to confirm both are
        // served independently rather than the second hitting a stale entry
        // for the first.
        var rangeA =
            $"{new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()},"
            + new DateTimeOffset(2023, 1, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rangeB =
            $"{new DateTimeOffset(2023, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()},"
            + new DateTimeOffset(2023, 2, 28, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var responseA = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt?time={rangeA}");
        var responseB = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt?time={rangeB}");

        responseA.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        responseB.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // The HTTP cache vary-by-query change for #379 is the only thing that
        // keeps the second request from being served the first request's body.
        // If a future change drops `time` from the cache policy, both responses
        // will be byte-identical even with different filters.
        if (responseA.StatusCode == HttpStatusCode.OK && responseB.StatusCode == HttpStatusCode.OK)
        {
            var bytesA = await responseA.Content.ReadAsByteArrayAsync();
            var bytesB = await responseB.Content.ReadAsByteArrayAsync();

            // Tile bodies for distinct temporal subsets must not silently share
            // a cache slot — they may legitimately differ in length or content.
            // We assert at minimum that the second response is not a cached
            // copy of the first when the underlying data differs.
            (bytesA.Length != bytesB.Length || !bytesA.AsSpan().SequenceEqual(bytesB))
                .Should()
                .BeTrue("MvtTile cache must vary by `time` parameter (#379)");
        }
    }
}
