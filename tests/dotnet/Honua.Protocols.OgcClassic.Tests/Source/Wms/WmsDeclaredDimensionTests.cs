// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class WmsDeclaredDimensionTests : IAsyncLifetime
{
    private const string FixtureLayerName = "cite:Autos";
    private const string DeclaredTimeExtent = "2000-01-01T00:00:00Z/2000-01-01T00:01:00Z/PT5S";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string WmsUrl(string query)
        => $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0&{query}";

    private static string GetMapQuery(string time)
        => $"REQUEST=GetMap&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TIME={time}";

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_DeclaredDimensions_AreAdvertisedFromMetadata()
    {
        _fixture.SetV2ResourceExtension(WebAppFixture.TestLayerId, "wms", DeclaredDimensionJson.WmsTime);
        var timeContent = await (await _fixture.Client.GetAsync(WmsUrl("REQUEST=GetCapabilities"))).Content.ReadAsStringAsync();

        _fixture.SetV2ResourceExtension(WebAppFixture.TestLayerId, "wms", DeclaredDimensionJson.WmsElevation);
        var elevationContent = await (await _fixture.Client.GetAsync(WmsUrl("REQUEST=GetCapabilities"))).Content.ReadAsStringAsync();

        timeContent.Should().Contain(
            $"<Dimension name=\"time\" units=\"ISO8601\" multipleValues=\"true\" nearestValue=\"true\" default=\"2000-01-01T00:00:30Z\">{DeclaredTimeExtent}</Dimension>");
        elevationContent.Should().Contain(
            "<Dimension name=\"elevation\" units=\"CRS:88\" unitSymbol=\"m\" multipleValues=\"false\" nearestValue=\"true\" default=\"500\">500,490,480</Dimension>");
        elevationContent.Should().NotContain(DeclaredTimeExtent);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_LayerNamedLikeFixtureWithoutDeclaredDimension_GetsNoSpecialTreatment()
    {
        // The layer name alone must not select any dimension behaviour: only metadata does.
        _fixture.UpdateV2ResourceName(WebAppFixture.TestLayerId, FixtureLayerName);
        _fixture.MutateV2ResourceObjectMetadata(WebAppFixture.TestLayerId, metadata => metadata with { Title = FixtureLayerName });
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            temporal: new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });

        var capabilities = await _fixture.Client.GetAsync(WmsUrl("REQUEST=GetCapabilities"));
        var map = await _fixture.Client.GetAsync(WmsUrl(GetMapQuery("current")));

        var capabilitiesContent = await capabilities.Content.ReadAsStringAsync();
        capabilities.StatusCode.Should().Be(HttpStatusCode.OK, capabilitiesContent);
        capabilitiesContent.Should().NotContain(DeclaredTimeExtent);
        capabilitiesContent.Should().Contain("<Dimension name=\"time\" units=\"ISO8601\" multipleValues=\"false\"");

        // A time-aware layer without a declared dimension parses TIME, so "current" is rejected.
        var mapContent = await map.Content.ReadAsStringAsync();
        map.StatusCode.Should().Be(HttpStatusCode.BadRequest, mapContent);
        mapContent.Should().Contain("InvalidDimensionValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_MalformedDeclaredDimensions_AreIgnored()
    {
        _fixture.SetV2ResourceExtension(WebAppFixture.TestLayerId, "wms", """{"dimensions":[{"units":"m"},"elevation",7]}""");

        var response = await _fixture.Client.GetAsync(WmsUrl("REQUEST=GetCapabilities"));

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("<Dimension name=\"elevation\"");
    }
}
