// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.CrossServerConsume;

/// <summary>
/// Cross-server consume checks for Honua reading OGC services from MapServer.
/// </summary>
[Collection("ExternalServer")]
[Protocol(TestProtocols.Wms13)]
[Protocol(TestProtocols.Wfs20)]
[Protocol(TestProtocols.Wmts10)]
[Operation(Operations.Consume)]
[Trait("Suite", "CrossServerConsume")]
public sealed class CrossServerConsumeMapServerTests : IClassFixture<CrossServerConsumeMapServerFixture>
{
    private const string MapServerWmtsGap = "gap: camptocamp/mapserver:8.0 exposes WMS/WFS but does not include WMTS_SERVER support; add a MapCache-backed reference source for WMTS.";
    private readonly CrossServerConsumeMapServerFixture _mapServer;

    public CrossServerConsumeMapServerTests(CrossServerConsumeMapServerFixture mapServer)
    {
        _mapServer = mapServer;
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetCapabilities_MapServer_ReturnsLayerDocument()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _mapServer.HonuaClient,
            BuildProxyUrl(
                _mapServer.EndpointUrl,
                ("SERVICE", "WMS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.3.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "WMS_Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, MapServerFixture.LayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetMap_MapServer_ReturnsImageForKnownLayer()
    {
        var image = await CrossServerConsumeTestSupport.GetImageAsync(
            _mapServer.HonuaClient,
            BuildProxyUrl(
                _mapServer.EndpointUrl,
                ("SERVICE", "WMS"),
                ("VERSION", "1.3.0"),
                ("REQUEST", "GetMap"),
                ("LAYERS", MapServerFixture.LayerName),
                ("STYLES", string.Empty),
                ("CRS", "EPSG:4326"),
                ("BBOX", CrossServerConsumeTestSupport.HawaiiWms13Bbox4326),
                ("WIDTH", "256"),
                ("HEIGHT", "256"),
                ("FORMAT", "image/png")));

        image.Should().NotBeEmpty();
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetFeatureInfo_MapServer_ReturnsFeatureInfoPayload()
    {
        await CrossServerConsumeTestSupport.GetTextAsync(
            _mapServer.HonuaClient,
            BuildProxyUrl(
                _mapServer.EndpointUrl,
                ("SERVICE", "WMS"),
                ("VERSION", "1.3.0"),
                ("REQUEST", "GetFeatureInfo"),
                ("LAYERS", MapServerFixture.LayerName),
                ("QUERY_LAYERS", MapServerFixture.LayerName),
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
    public async Task WfsGetCapabilities_MapServer_ReturnsFeatureTypeDocument()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _mapServer.HonuaClient,
            BuildProxyUrl(
                _mapServer.EndpointUrl,
                ("SERVICE", "WFS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "2.0.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "WFS_Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, MapServerFixture.LayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv)]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Consume)]
    public async Task WfsGetFeature_MapServer_ReturnsExpectedFeatures()
    {
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _mapServer.HonuaClient,
            BuildProxyUrl(
                _mapServer.EndpointUrl,
                ("SERVICE", "WFS"),
                ("VERSION", "2.0.0"),
                ("REQUEST", "GetFeature"),
                ("TYPENAMES", MapServerFixture.LayerName),
                ("COUNT", "1")));

        CrossServerConsumeTestSupport.AssertFeatureCollectionHasFeature(document, MapServerFixture.LayerName);
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv, Skip = MapServerWmtsGap)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Consume)]
    public Task WmtsGetCapabilities_MapServer_ReturnsLayerDocument()
    {
        return Task.CompletedTask;
    }

    [ExternalServiceTest(CrossServerConsumeTestSupport.ExternalServicesEnv, Skip = MapServerWmtsGap)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Consume)]
    public Task WmtsGetTile_MapServer_ReturnsAdvertisedTile()
    {
        return Task.CompletedTask;
    }

    private static string BuildProxyUrl(string baseUrl, params (string Name, string Value)[] parameters)
        => CrossServerConsumeTestSupport.BuildHonuaProxyUrl(
            CrossServerConsumeTestSupport.BuildUrl(baseUrl, parameters));
}
