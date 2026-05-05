// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

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

    private Task ConfigureLayerAsTimeAwareAsync()
    {
        var updater = _fixture.GetService<ILayerMetadataUpdater>();
        return updater.UpdateLayerMetadataAsync(
            WebAppFixture.TestLayerId,
            new CatalogMetadata
            {
                TimeInfo = new LayerTimeInfo { StartTimeField = "created_at" }
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
