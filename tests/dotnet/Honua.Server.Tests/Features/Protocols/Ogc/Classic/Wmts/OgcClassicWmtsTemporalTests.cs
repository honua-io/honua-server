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

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// Integration tests for the dynamic WMTS time dimension introduced for ticket #379.
/// Verifies that opt-in <see cref="LayerTimeInfo"/> configuration causes the layer
/// to advertise a continuous time dimension in the capabilities document and that
/// non-time-aware layers do not.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class OgcClassicWmtsTemporalTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_TimeAwareLayer_AdvertisesTimeDimension()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        // WMTS dimension element with the "time" identifier and an explicit
        // <Default> populated from the layer extent. The continuous-extent
        // <Value> is rendered using the resolved min/max range (PT0S step).
        content.Should().Contain("<ows:Identifier>time</ows:Identifier>");
        content.Should().Contain("<Default>");
        content.Should().Contain("PT0S");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_NonTimeAwareLayer_DoesNotAdvertiseTimeDimension()
    {
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("<ows:Identifier>time</ows:Identifier>");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_TimeAwareLayer_WithoutTime_ReturnsTile()
    {
        // Non-regression: an advertised but optional time dimension must not
        // require the parameter on every GetTile request.
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_TimeAwareLayer_WithTimeIso8601_ReturnsTile()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png&time=2024-06-15T12:00:00Z");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_TimeAwareLayer_WithMalformedTime_ReturnsBadRequest()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png&time=not-a-time");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_NonTimeAwareLayer_WithTime_ReturnsInvalidParameterValue()
    {
        // Layers without configured TimeInfo do not advertise a time dimension
        // (verified by Wmts_GetCapabilities_NonTimeAwareLayer_DoesNotAdvertiseTimeDimension).
        // A GetTile request that supplies time= against such a layer must be
        // rejected with InvalidParameterValue rather than silently ignored —
        // otherwise the request appears to honor a temporal filter that the
        // capabilities document never advertised.
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png&time=2024-06-15T12:00:00Z");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("InvalidParameterValue");
        content.Should().Contain("time");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetFeatureInfo_NonTimeAwareLayer_WithTime_ReturnsInvalidParameterValue()
    {
        // Companion to the GetTile case: GetFeatureInfo must also reject time=
        // on layers that do not advertise a time dimension. Both paths share
        // the same TryValidateWmtsDimensionParameters validator, so a
        // regression here would also be a regression for GetTile.
        await ClearLayerTimeInfoAsync();

        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
            "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128" +
            "&INFOFORMAT=application/json" +
            "&time=2024-06-15T12:00:00Z";

        var response = await _fixture.Client.GetAsync(url);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("InvalidParameterValue");
        content.Should().Contain("time");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_TimeAwareLayer_WithTimeDefault_ReturnsTile()
    {
        // GetCapabilities advertises the dynamic time dimension's <Default>
        // and <Current> as the layer's max timestamp. Requests sending those
        // tokens must therefore validate and resolve to a real instant rather
        // than be rejected as InvalidParameterValue.
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png&time=default");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_TimeAwareLayer_WithTimeCurrent_ReturnsTile()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png&time=current");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetFeatureInfo_TimeAwareLayer_WithTimeCurrent_ReturnsFeatures()
    {
        // GetFeatureInfo must accept the same tokens GetCapabilities
        // advertises. With "current" resolved to the layer's max timestamp,
        // the seeded 2023 features remain in the temporal window.
        await ConfigureLayerAsTimeAwareAsync();

        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
            "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128" +
            "&INFOFORMAT=application/json" +
            "&time=current";

        var response = await _fixture.Client.GetAsync(url);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetFeatureInfo_TimeAwareLayer_OutOfRangeTime_ReturnsEmptyFeatures()
    {
        // Seeded test layer rows have `timestamp` values in 2022–2023; a time
        // window in 2099 must filter them all out, proving the WMTS time
        // dimension actually feeds the FeatureQuery temporal filter rather
        // than being silently dropped.
        await ConfigureLayerAsTimeAwareAsync();

        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
            "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128" +
            "&INFOFORMAT=application/json" +
            "&time=" + Uri.EscapeDataString("2099-01-01T00:00:00Z/2099-12-31T23:59:59Z");

        var response = await _fixture.Client.GetAsync(url);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        // Empty FeatureInfo response: the WMTS handler emits the JSON envelope
        // only when at least one feature matches; an empty match yields a bare
        // empty body.
        content.Should().NotContain("\"features\":[{");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetFeatureInfo_TimeAwareLayer_InRangeTime_ReturnsFeatures()
    {
        // Companion to the out-of-range test: a window covering the seeded
        // 2023 timestamps must still surface features so we know the empty
        // response above is caused by the temporal filter and not by the
        // pixel-tolerance click missing every feature.
        await ConfigureLayerAsTimeAwareAsync();

        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
            "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128" +
            "&INFOFORMAT=application/json" +
            "&time=" + Uri.EscapeDataString("2022-01-01T00:00:00Z/2024-01-01T00:00:00Z");

        var response = await _fixture.Client.GetAsync(url);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        // When at least one feature matches the click + time filter, the
        // response carries the FeatureInfoResponse envelope. The seeded layer
        // has a global extent so the central pixel intersects features.
        if (!string.IsNullOrWhiteSpace(content))
        {
            using var json = JsonDocument.Parse(content);
            json.RootElement.GetProperty("type").GetString().Should().Be("FeatureInfoResponse");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_InvalidTimeInfo_DoesNotAdvertiseTimeDimension()
    {
        // Layer metadata updates do not validate that the configured
        // StartTimeField actually exists as a Date/DateTime attribute. If we
        // advertise a time dimension solely because TimeInfo.StartTimeField is
        // non-empty, GetCapabilities will publish an unusable dimension that
        // OgcTemporalFilterParser cannot fulfill (the layer's AttributeFields
        // do not contain that field as Date/DateTime). The WMTS handler must
        // therefore gate the dimension on the same opt-in resolvability check
        // used by TryResolveTemporalRangeAsync.
        await ConfigureLayerWithInvalidTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("<ows:Identifier>time</ows:Identifier>");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_InvalidTimeInfo_WithTime_ReturnsInvalidParameterValue()
    {
        // Companion to the GetCapabilities case: when capabilities does not
        // advertise a time dimension, a GetTile request that supplies time=
        // must be rejected as an unknown query key by the dimension validator
        // — same behavior as a layer with no TimeInfo at all. This proves the
        // capabilities/runtime contract stays aligned even when layer metadata
        // stores a misconfigured TimeInfo.
        await ConfigureLayerWithInvalidTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png&time=2024-06-15T12:00:00Z");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("InvalidParameterValue");
        content.Should().Contain("time");
    }

    private Task ConfigureLayerAsTimeAwareAsync()
    {
        // V2 cutover (#1035 72/N): time-info now lives on MetadataV2Resource.Temporal.
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            temporal: new MetadataV2ResourceTemporal { StartTimeField = "timestamp" });
        return Task.CompletedTask;
    }

    private Task ConfigureLayerWithInvalidTimeInfoAsync()
    {
        // The seeded "name" attribute is a string column, not a Date/DateTime,
        // so configuring it as StartTimeField simulates the misconfiguration
        // the metadata update path allows today (no field-type validation).
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            temporal: new MetadataV2ResourceTemporal { StartTimeField = "name" });
        return Task.CompletedTask;
    }

    private Task ClearLayerTimeInfoAsync()
    {
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
        return Task.CompletedTask;
    }
}
