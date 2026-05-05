// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Integration tests for the dynamic WMS TIME dimension introduced for ticket #379.
/// Configures the test layer with explicit <see cref="LayerTimeInfo"/> metadata so
/// the dimension is opt-in (fallback-only layers do not advertise a time dimension)
/// and exercises both GetCapabilities advertising and GetMap TIME parameter
/// acceptance/rejection.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsTemporalTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_TimeAwareLayer_AdvertisesTimeDimension()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        // Continuous time dimension with ISO 8601 units; the asterisked default and
        // extent are populated from the layer's resolved temporal range.
        content.Should().Contain("<Dimension name=\"time\" units=\"ISO8601\"");
        content.Should().Contain("multipleValues=\"false\"");
        content.Should().Contain("nearestValue=\"true\"");
        // PT0S indicates a continuous interval (no enumerated step) per the design.
        content.Should().Contain("PT0S</Dimension>");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_NonTimeAwareLayer_DoesNotAdvertiseTimeDimension()
    {
        // Without explicit TimeInfo metadata the layer must NOT advertise the time
        // dimension, even though the seeded schema has DateTime/Date columns.
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("<Dimension name=\"time\"");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_TimeAwareLayer_WithIsoInstant_ReturnsImage()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME=2024-06-15T12:00:00Z");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_TimeAwareLayer_WithIso8601Interval_ReturnsImage()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME=2024-01-01T00:00:00Z/2024-12-31T23:59:59Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_TimeAwareLayer_WithMalformedTime_ReturnsServiceException()
    {
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME=not-a-time");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidDimensionValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_NonTimeAwareLayer_WithTime_ReturnsServiceException()
    {
        await ClearLayerTimeInfoAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME=2024-06-15T12:00:00Z");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidDimensionValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_TimeAwareLayer_WithoutTime_ReturnsImage()
    {
        // Non-regression: TIME is optional. Omitting it must still return imagery.
        await ConfigureLayerAsTimeAwareAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_CiteAutosLayer_WithTimeCurrent_BypassesGenericParser()
    {
        // CITE Autos rendering parses TIME directly (current/CSV/intervals) in
        // TryHandleCiteWmsGetMap; the generic OgcTemporalFilterParser must be
        // bypassed when the request targets that layer or it would reject
        // CITE-supported tokens like "current" before the CITE branch runs.
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE honua.layers SET layer_name = @layerName WHERE layer_id = @layerId;";
            command.Parameters.Add(new NpgsqlParameter { ParameterName = "layerName", Value = "cite:Autos" });
            command.Parameters.Add(new NpgsqlParameter { ParameterName = "layerId", Value = WebAppFixture.TestLayerId });
            await command.ExecuteNonQueryAsync();
        }

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME=current");

        // Without the bypass this would be HTTP 400 with InvalidDimensionValue
        // because OgcTemporalFilterParser cannot parse "current". The bypass
        // lets the request fall through to standard rendering (the test
        // service is not the CITE service, so synthetic CITE rendering is
        // skipped and the response is the layer's full-extent image).
        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_CiteAutosLayer_WithCsvTimeInstants_BypassesGenericParser()
    {
        // Comma-separated TIME instants are CITE-supported but rejected by
        // OgcTemporalFilterParser (only RFC 3339 instants/intervals). Verify
        // the bypass also covers this CITE-only form.
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE honua.layers SET layer_name = @layerName WHERE layer_id = @layerId;";
            command.Parameters.Add(new NpgsqlParameter { ParameterName = "layerName", Value = "cite:Autos" });
            command.Parameters.Add(new NpgsqlParameter { ParameterName = "layerId", Value = WebAppFixture.TestLayerId });
            await command.ExecuteNonQueryAsync();
        }

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME=2000-01-01T00:00:00Z,2000-01-01T00:00:30Z");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
    }

    private Task ConfigureLayerAsTimeAwareAsync()
    {
        // Use the seeded "timestamp" DateTime field that is registered in
        // honua.layer_fields for the shared test layer; the helper resolves
        // an extent only when the configured field is a real attribute.
        var updater = _fixture.GetService<ILayerMetadataUpdater>();
        return updater.UpdateLayerMetadataAsync(
            WebAppFixture.TestLayerId,
            new CatalogMetadata
            {
                TimeInfo = new LayerTimeInfo { StartTimeField = "timestamp" }
            });
    }

    private Task ClearLayerTimeInfoAsync()
    {
        var updater = _fixture.GetService<ILayerMetadataUpdater>();
        return updater.UpdateLayerMetadataAsync(
            WebAppFixture.TestLayerId,
            new CatalogMetadata());
    }
}
