// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.CrossServerConsume;

/// <summary>
/// Cross-server consume checks for Honua reading OGC services from GeoServer.
/// </summary>
[Collection("ExternalServer")]
[Protocol(TestProtocols.Wms13)]
[Protocol(TestProtocols.Wfs20)]
[Protocol(TestProtocols.Wmts10)]
[Operation(Operations.Consume)]
[Trait("Suite", "CrossServerConsume")]
public sealed class CrossServerConsumeGeoServerTests : IClassFixture<CrossServerConsumeGeoServerFixture>
{
    private readonly CrossServerConsumeGeoServerFixture _geoServer;

    private string OwsEndpoint => $"{_geoServer.BaseUrl}/geoserver/{GeoServerFixture.CuratedWorkspaceName}/ows";

    private string WmtsEndpoint => $"{_geoServer.BaseUrl}/geoserver/gwc/service/wmts";

    public CrossServerConsumeGeoServerTests(CrossServerConsumeGeoServerFixture geoServer)
    {
        _geoServer = geoServer;
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetCapabilities_GeoServer_ReturnsLayerDocument()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                OwsEndpoint,
                ("SERVICE", "WMS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.3.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "WMS_Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, GeoServerFixture.CuratedLayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetMap_GeoServer_ReturnsImageForKnownLayer()
    {
        var image = await CrossServerConsumeTestSupport.GetImageAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                OwsEndpoint,
                ("SERVICE", "WMS"),
                ("VERSION", "1.3.0"),
                ("REQUEST", "GetMap"),
                ("LAYERS", GeoServerFixture.CuratedQualifiedLayerName),
                ("STYLES", string.Empty),
                ("CRS", "EPSG:4326"),
                ("BBOX", CrossServerConsumeTestSupport.HawaiiWms13Bbox4326),
                ("WIDTH", "256"),
                ("HEIGHT", "256"),
                ("FORMAT", "image/png"),
                ("TRANSPARENT", "true")));

        image.Should().NotBeEmpty();
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetFeatureInfo_GeoServer_ReturnsFeatureInfoPayload()
    {
        await CrossServerConsumeTestSupport.GetTextAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                OwsEndpoint,
                ("SERVICE", "WMS"),
                ("VERSION", "1.3.0"),
                ("REQUEST", "GetFeatureInfo"),
                ("LAYERS", GeoServerFixture.CuratedQualifiedLayerName),
                ("QUERY_LAYERS", GeoServerFixture.CuratedQualifiedLayerName),
                ("STYLES", string.Empty),
                ("CRS", "EPSG:4326"),
                ("BBOX", CrossServerConsumeTestSupport.HawaiiWms13Bbox4326),
                ("WIDTH", "256"),
                ("HEIGHT", "256"),
                ("I", "128"),
                ("J", "128"),
                ("FORMAT", "image/png"),
                ("INFO_FORMAT", "text/plain"),
                ("FEATURE_COUNT", "5")));
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Consume)]
    public async Task WfsGetCapabilities_GeoServer_ReturnsFeatureTypeDocument()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                OwsEndpoint,
                ("SERVICE", "WFS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "2.0.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "WFS_Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, GeoServerFixture.CuratedQualifiedLayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Consume)]
    public async Task WfsGetFeature_GeoServer_ReturnsExpectedFeatures()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                OwsEndpoint,
                ("SERVICE", "WFS"),
                ("VERSION", "2.0.0"),
                ("REQUEST", "GetFeature"),
                ("TYPENAMES", GeoServerFixture.CuratedQualifiedLayerName),
                ("COUNT", "1")));

        CrossServerConsumeTestSupport.AssertFeatureCollectionHasFeature(document, GeoServerFixture.CuratedLayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Consume)]
    public async Task WmtsGetCapabilities_GeoServer_ReturnsLayerDocument()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                WmtsEndpoint,
                ("SERVICE", "WMTS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.0.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, GeoServerFixture.CuratedQualifiedLayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Consume)]
    public async Task WmtsGetTile_GeoServer_ReturnsAdvertisedTile()
    {
        var capabilities = await CrossServerConsumeTestSupport.GetXmlAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                WmtsEndpoint,
                ("SERVICE", "WMTS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.0.0")));
        var tileRequest = CrossServerConsumeTestSupport.SelectFirstAdvertisedTile(
            capabilities,
            GeoServerFixture.CuratedQualifiedLayerName);

        var image = await CrossServerConsumeTestSupport.GetImageAsync(
            _geoServer.HonuaClient,
            BuildProxyUrl(
                WmtsEndpoint,
                ("SERVICE", "WMTS"),
                ("REQUEST", "GetTile"),
                ("VERSION", "1.0.0"),
                ("LAYER", tileRequest.Layer),
                ("STYLE", tileRequest.Style),
                ("FORMAT", tileRequest.Format),
                ("TILEMATRIXSET", tileRequest.TileMatrixSet),
                ("TILEMATRIX", tileRequest.TileMatrix),
                ("TILEROW", tileRequest.TileRow.ToString(CultureInfo.InvariantCulture)),
                ("TILECOL", tileRequest.TileCol.ToString(CultureInfo.InvariantCulture))));

        image.Should().NotBeEmpty();
    }

    private static string BuildProxyUrl(string baseUrl, params (string Name, string Value)[] parameters)
        => CrossServerConsumeTestSupport.BuildHonuaProxyUrl(
            CrossServerConsumeTestSupport.BuildUrl(baseUrl, parameters));
}
