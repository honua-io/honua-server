// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class WmtsDeclaredDimensionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string WmtsUrl(string query)
        => $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&VERSION=1.0.0&{query}";

    private static string TileQuery(string request, string suffix = "")
        => $"REQUEST={request}&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
           $"&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0{suffix}";

    private const string FeatureInfoSuffix = "&I=128&J=128&INFOFORMAT=text/plain";
    private static readonly XNamespace WmtsNs = "http://www.opengis.net/wmts/1.0";
    private static readonly XNamespace OwsNs = "http://www.opengis.net/ows/1.1";

    private async Task<XElement> GetCapabilitiesLayerAsync()
    {
        var response = await _fixture.Client.GetAsync(WmtsUrl("REQUEST=GetCapabilities"));
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identifier = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        return XDocument.Parse(content).Descendants(WmtsNs + "Layer")
            .Single(layer => layer.Element(OwsNs + "Identifier")?.Value == identifier);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    public async Task Wmts_GetCapabilities_DeclaredDimensions_AreAdvertisedFromMetadata()
    {
        _fixture.SetV2ResourceExtension(WebAppFixture.TestLayerId, "wmts", DeclaredDimensionJson.WmtsElevationAndScenario);

        var response = await _fixture.Client.GetAsync(WmtsUrl("REQUEST=GetCapabilities"));

        var content = (await response.Content.ReadAsStringAsync()).ReplaceLineEndings("\n");
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain(
            "      <Dimension>\n        <ows:Identifier>elevation</ows:Identifier>\n        <Default>100</Default>\n" +
            "        <Current>true</Current>\n        <Value>100</Value>\n        <Value>200</Value>\n        <Value>300</Value>\n      </Dimension>\n" +
            "      <Dimension>\n        <ows:Identifier>scenario</ows:Identifier>\n        <Default>winter</Default>\n" +
            "        <Value>winter</Value>\n        <Value>summer</Value>\n      </Dimension>\n");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    public async Task Wmts_GetTile_DeclaredDimension_ValidatesRequestedValue()
    {
        // The layer is time-aware, yet TIME is not parsed for it once it declares dimensions.
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            temporal: new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });
        _fixture.SetV2ResourceExtension(WebAppFixture.TestLayerId, "wmts", DeclaredDimensionJson.WmtsElevationAndScenario);

        var advertised = await _fixture.Client.GetAsync(WmtsUrl(TileQuery("GetTile", "&elevation=current&scenario=summer&time=not-a-time")));
        var unknown = await _fixture.Client.GetAsync(WmtsUrl(TileQuery("GetTile", "&elevation=999")));
        var layer = await GetCapabilitiesLayerAsync();

        layer.Elements(WmtsNs + "Dimension").Should().HaveCount(2, "a data-derived time dimension is not added to declared dimensions");
        advertised.StatusCode.Should().Be(HttpStatusCode.OK, await advertised.Content.ReadAsStringAsync());
        advertised.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        var unknownContent = await unknown.Content.ReadAsStringAsync();
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest, unknownContent);
        unknownContent.Should().Contain("InvalidParameterValue").And.Contain("elevation");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetFeatureInfo")]
    public async Task Wmts_QueryableFalse_DisablesGetFeatureInfoForTheLayer()
    {
        _fixture.SetV2ResourceExtension(WebAppFixture.TestLayerId, "wmts", DeclaredDimensionJson.WmtsNotQueryable);

        var featureInfo = await _fixture.Client.GetAsync(WmtsUrl(TileQuery("GetFeatureInfo", FeatureInfoSuffix)));
        var layer = await GetCapabilitiesLayerAsync();

        var featureInfoContent = await featureInfo.Content.ReadAsStringAsync();
        featureInfo.StatusCode.Should().Be(HttpStatusCode.NotImplemented, featureInfoContent);
        featureInfoContent.Should().Contain("OperationNotSupported");
        layer.Elements(WmtsNs + "InfoFormat").Should().BeEmpty();
        layer.Elements(WmtsNs + "ResourceURL").Should().NotContain(url => url.Attribute("resourceType")!.Value == "FeatureInfo");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetFeatureInfo")]
    public async Task Wmts_LayerNamedLikeFixtureWithoutMetadata_GetsNoSpecialTreatment()
    {
        // Neither fixture layer name may select non-queryable or dimension behaviour by itself.
        foreach (var fixtureName in new[] { "cite:BasicPolygons", "cite:Terrain" })
        {
            _fixture.UpdateV2ResourceName(WebAppFixture.TestLayerId, fixtureName);
            _fixture.MutateV2ResourceObjectMetadata(WebAppFixture.TestLayerId, metadata => metadata with { Title = fixtureName });

            var featureInfo = await _fixture.Client.GetAsync(WmtsUrl(TileQuery("GetFeatureInfo", FeatureInfoSuffix)));
            var layer = await GetCapabilitiesLayerAsync();

            featureInfo.StatusCode.Should().Be(HttpStatusCode.OK, await featureInfo.Content.ReadAsStringAsync());
            layer.Elements(WmtsNs + "InfoFormat").Should().NotBeEmpty();
            layer.Elements(WmtsNs + "Dimension").Should().BeEmpty();
        }
    }
}
