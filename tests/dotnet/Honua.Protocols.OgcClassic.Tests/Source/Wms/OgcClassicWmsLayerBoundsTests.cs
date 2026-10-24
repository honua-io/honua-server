// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// honua-server#6097: WMS LAYERS / QUERY_LAYERS are deduplicated by storage layer and
/// bounded, so one anonymous request cannot fan out into N feature scans and N renders
/// while holding a shared render-capacity lease.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsLayerBoundsTests : IAsyncLifetime
{
    private const string GetMapQuery =
        "?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&CRS=CRS:84&BBOX=-180,-90,180,90" +
        "&WIDTH=8&HEIGHT=8&FORMAT=image/png";

    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader>();
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(0, []));
        _fixture.ReplaceRequestService(_reader);
        await _fixture.InitializeAsync();
        // Keep the request on the plain render path: no TIME dimension resolution.
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("mapserver")]
    [InlineData("ogc")]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_GetMap_DuplicateLayers_QueriesFeatureReaderOnce(string route)
    {
        var layer = WebAppFixture.TestLayerId;
        var response = await _fixture.Client.GetAsync(
            WmsPath(route) + GetMapQuery + $"&LAYERS={layer},{layer},{layer}&STYLES=,default,");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        await _reader.Received(1).QueryAsync(layer, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData("mapserver")]
    [InlineData("ogc")]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_GetMap_LayersOverCap_ReturnsInvalidParameterValue(string route)
    {
        var layers = string.Join(',', Enumerable.Repeat(
            WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new QueryLimits().MaxMapLayersPerRequest + 1));

        var response = await _fixture.Client.GetAsync(
            WmsPath(route) + GetMapQuery + $"&LAYERS={layers}&STYLES=");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("ServiceExceptionReport");
        body.Should().Contain("InvalidParameterValue");
        body.Should().Contain("LAYERS");
        await _reader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_DuplicateQueryLayers_QueriesFeatureReaderOnce()
    {
        var layer = WebAppFixture.TestLayerId;
        var response = await _fixture.Client.GetAsync(
            WmsPath("mapserver") +
            "?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0&CRS=CRS:84&BBOX=-180,-90,180,90" +
            $"&WIDTH=256&HEIGHT=256&LAYERS={layer},{layer}&QUERY_LAYERS={layer},{layer},{layer}" +
            "&INFO_FORMAT=application/json&I=41&J=74");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        await _reader.Received(1).QueryAsync(layer, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_QueryLayersOverCap_ReturnsInvalidParameterValue()
    {
        var layer = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var queryLayers = string.Join(',', Enumerable.Repeat(layer, new QueryLimits().MaxMapLayersPerRequest + 1));

        var response = await _fixture.Client.GetAsync(
            WmsPath("mapserver") +
            "?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0&CRS=CRS:84&BBOX=-180,-90,180,90" +
            $"&WIDTH=256&HEIGHT=256&LAYERS={layer}&QUERY_LAYERS={queryLayers}" +
            "&INFO_FORMAT=application/json&I=41&J=74");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("ServiceExceptionReport");
        body.Should().Contain("InvalidParameterValue");
        await _reader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
    }

    private static string WmsPath(string route) => route switch
    {
        "ogc" => $"/ogc/services/{WebAppFixture.TestServiceId}/wms",
        _ => $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS"
    };
}
