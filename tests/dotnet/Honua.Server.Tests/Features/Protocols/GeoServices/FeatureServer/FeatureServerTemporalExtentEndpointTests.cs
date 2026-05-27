// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the temporal extent endpoint introduced for ticket #379.
/// Discovery is opt-in: layers without an explicit <see cref="LayerTimeInfo"/>
/// configuration must not surface a temporal extent even if they have a Date /
/// DateTime column. The happy path therefore configures TimeInfo via the
/// admin metadata updater before issuing the request.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerTemporalExtentEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_TimeAwareLayer_ReturnsExtentWithFields()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("layerId").GetInt32().Should().Be(WebAppFixture.TestLayerId);
        root.TryGetProperty("startTimeField", out var startField).Should().BeTrue();
        startField.ValueKind.Should().Be(JsonValueKind.String);
        startField.GetString().Should().NotBeNullOrWhiteSpace();

        // Min/max may be either ISO 8601 or null when no rows exist; but the
        // base seed populates rows so we expect a non-null pair.
        root.TryGetProperty("min", out var min).Should().BeTrue();
        root.TryGetProperty("max", out var max).Should().BeTrue();
        min.ValueKind.Should().Be(JsonValueKind.String);
        max.ValueKind.Should().Be(JsonValueKind.String);

        // Epoch ms variant must mirror the ISO timestamps for ArcGIS-compatible clients.
        root.TryGetProperty("minEpochMs", out var minEpoch).Should().BeTrue();
        root.TryGetProperty("maxEpochMs", out var maxEpoch).Should().BeTrue();
        minEpoch.ValueKind.Should().Be(JsonValueKind.Number);
        maxEpoch.ValueKind.Should().Be(JsonValueKind.Number);
        maxEpoch.GetInt64().Should().BeGreaterThanOrEqualTo(minEpoch.GetInt64());
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_NonexistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/9999/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_NonexistentService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_UnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=xml");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_DisallowedQueryParameter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?where=1=1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_LayerWithoutTimeInfo_ReturnsNotFound()
    {
        // Discovery is opt-in: a layer with a Date / DateTime column but no
        // TimeInfo.StartTimeField must NOT surface a temporal extent. The
        // shared TryResolveTemporalRangeAsync helper falls back to the first
        // temporal attribute for OGC API Features collection metadata; the
        // FeatureServer endpoint guards against that fallback so SDK clients
        // do not infer "time-aware" from arbitrary date columns. Other tests
        // in the shared `Database` collection (WMS / WMTS temporal suites)
        // may leave TimeInfo set on the same layer, so clear it first.
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_LayerWithoutTimeInfo_DoesNotEmitTimeInfo()
    {
        // Companion to TemporalExtent_LayerWithoutTimeInfo_ReturnsNotFound:
        // FeatureServer layer metadata must mirror the same opt-in contract
        // (docs/gis/temporal-animation-api.md). Layers with a Date / DateTime
        // column but no TimeInfo.StartTimeField must NOT publish timeInfo on
        // /rest/services/{id}/FeatureServer/{layerId} either, otherwise SDK
        // clients would infer "time-aware" from arbitrary date columns and
        // diverge from the temporalExtent endpoint and the WMS/WMTS time
        // dimension contract that already gate on opt-in.
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        if (root.TryGetProperty("timeInfo", out var timeInfoElement))
        {
            timeInfoElement.ValueKind.Should().Be(JsonValueKind.Null,
                "non-time-aware layers must not publish a timeInfo block");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_NonTimeAwareLayer_WithTime_ReturnsBadRequest()
    {
        // The temporal-animation contract requires query?time= to reject
        // non-time-aware layers with HTTP 400 (docs/gis/temporal-animation-api.md
        // "Empty-range and non-time-aware behavior" table). The previous
        // GeoServicesTemporalQueryBuilder fell back to the first Date /
        // DateTime attribute when TimeInfo was absent, silently filtering on
        // a non-temporal column instead of rejecting the request — same gap
        // the MVT path used to have.
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query"
            + "?time=2024-06-15T12:00:00Z&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_NonTimeAwareLayer_WithTimeNullPair_ReturnsOk()
    {
        // Non-regression: time=null,null is the documented no-op (treated as
        // "no temporal filter") — it must not trip the new opt-in gate so
        // SDK clients can pass a parameter unconditionally.
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query"
            + "?time=null,null&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_TimeAwareLayer_EmitsTimeInfo()
    {
        // Companion confirmation that opt-in layers still publish the timeInfo
        // block — the new opt-in gate must not regress configured layers.
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("timeInfo", out var timeInfoElement).Should().BeTrue();
        timeInfoElement.ValueKind.Should().Be(JsonValueKind.Object);
        timeInfoElement.GetProperty("startTimeField").GetString()
            .Should().Be("timestamp");
    }

    private Task ConfigureLayerAsTimeAwareAsync()
    {
        // V2 cutover (#1035 72/N): TimeInfo now lives on MetadataV2Resource.Temporal.
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            temporal: new MetadataV2ResourceTemporal { StartTimeField = "timestamp" });
        return Task.CompletedTask;
    }

    private Task ClearLayerTimeInfoAsync()
    {
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
        return Task.CompletedTask;
    }
}
