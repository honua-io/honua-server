// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
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
