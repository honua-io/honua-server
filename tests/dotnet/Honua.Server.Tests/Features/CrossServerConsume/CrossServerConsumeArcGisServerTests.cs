// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.CrossServerConsume;

/// <summary>
/// License-gated cross-server consume checks for Honua reading ArcGIS Server services.
/// </summary>
[Collection("ExternalServer")]
[Protocol(TestProtocols.Wms13)]
[Protocol(TestProtocols.Wfs20)]
[Protocol(TestProtocols.Wmts10)]
[Protocol(TestProtocols.MapServer)]
[Operation(Operations.Consume)]
[Trait("Suite", "CrossServerConsume")]
public sealed class CrossServerConsumeArcGisServerTests : IClassFixture<CrossServerConsumeArcGisServerFixture>
{
    private readonly CrossServerConsumeArcGisServerFixture _arcGis;

    public CrossServerConsumeArcGisServerTests(CrossServerConsumeArcGisServerFixture arcGis)
    {
        _arcGis = arcGis;
    }

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.WmsUrlEnv,
        ArcGisServerConsumeConfiguration.WmsLayerEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetCapabilities_ArcGisServer_ReturnsLayerDocument()
    {
        var config = _arcGis.Configuration;
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WmsUrl!,
                ("SERVICE", "WMS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.3.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "WMS_Capabilities");
        CrossServerConsumeTestSupport.AssertWmsLayerAdvertised(document, config.WmsLayer!);
    }

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.WmsUrlEnv,
        ArcGisServerConsumeConfiguration.WmsLayerEnv,
        ArcGisServerConsumeConfiguration.WmsBboxEnv)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Consume)]
    public async Task WmsGetMap_ArcGisServer_ReturnsImageForKnownLayer()
    {
        var config = _arcGis.Configuration;
        var image = await CrossServerConsumeTestSupport.GetImageAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WmsUrl!,
                ("SERVICE", "WMS"),
                ("VERSION", "1.3.0"),
                ("REQUEST", "GetMap"),
                ("LAYERS", config.WmsLayer!),
                ("STYLES", string.Empty),
                ("CRS", config.WmsCrs),
                ("BBOX", config.WmsBbox!),
                ("WIDTH", "256"),
                ("HEIGHT", "256"),
                ("FORMAT", config.WmsFormat),
                ("TRANSPARENT", "true")));

        image.Should().NotBeEmpty();
    }

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.WfsUrlEnv,
        ArcGisServerConsumeConfiguration.WfsTypeNameEnv)]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Consume)]
    public async Task WfsGetCapabilities_ArcGisServer_ReturnsFeatureTypeDocument()
    {
        var config = _arcGis.Configuration;
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WfsUrl!,
                ("SERVICE", "WFS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "2.0.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "WFS_Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, config.WfsTypeName!);
    }

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.WfsUrlEnv,
        ArcGisServerConsumeConfiguration.WfsTypeNameEnv)]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Consume)]
    public async Task WfsGetFeature_ArcGisServer_ReturnsExpectedFeatures()
    {
        var config = _arcGis.Configuration;
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WfsUrl!,
                ("SERVICE", "WFS"),
                ("VERSION", "2.0.0"),
                ("REQUEST", "GetFeature"),
                ("TYPENAMES", config.WfsTypeName!),
                ("COUNT", "1")));

        CrossServerConsumeTestSupport.AssertFeatureCollectionHasFeature(document, config.WfsTypeName!);
    }

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.WmtsUrlEnv,
        ArcGisServerConsumeConfiguration.WmtsLayerEnv)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Consume)]
    public async Task WmtsGetCapabilities_ArcGisServer_ReturnsLayerDocument()
    {
        var config = _arcGis.Configuration;
        var document = await CrossServerConsumeTestSupport.GetXmlAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WmtsUrl!,
                ("SERVICE", "WMTS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.0.0")));

        CrossServerConsumeTestSupport.AssertRoot(document, "Capabilities");
        CrossServerConsumeTestSupport.AssertDocumentContains(document, config.WmtsLayer!);
    }

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.WmtsUrlEnv,
        ArcGisServerConsumeConfiguration.WmtsLayerEnv)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Consume)]
    public async Task WmtsGetTile_ArcGisServer_ReturnsAdvertisedTile()
    {
        var config = _arcGis.Configuration;
        var capabilities = await CrossServerConsumeTestSupport.GetXmlAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WmtsUrl!,
                ("SERVICE", "WMTS"),
                ("REQUEST", "GetCapabilities"),
                ("VERSION", "1.0.0")));
        var tileRequest = CrossServerConsumeTestSupport.SelectFirstAdvertisedTile(
            capabilities,
            config.WmtsLayer!);

        var image = await CrossServerConsumeTestSupport.GetImageAsync(
            _arcGis.HonuaClient,
            BuildProxyUrl(
                config.WmtsUrl!,
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

    [ExternalServiceTest(
        CrossServerConsumeTestSupport.ExternalServicesEnv,
        ArcGisServerConsumeConfiguration.LicensedConsumeEnv,
        ArcGisServerConsumeConfiguration.MapServerTileUrlEnv)]
    [Protocol(TestProtocols.MapServer)]
    [Operation(Operations.Consume)]
    public async Task MapServerTile_ArcGisServer_ReturnsConfiguredTile()
    {
        var image = await CrossServerConsumeTestSupport.GetImageAsync(
            _arcGis.HonuaClient,
            CrossServerConsumeTestSupport.BuildHonuaProxyUrl(_arcGis.Configuration.MapServerTileUrl!));

        image.Should().NotBeEmpty();
    }

    private static string BuildProxyUrl(string baseUrl, params (string Name, string Value)[] parameters)
        => CrossServerConsumeTestSupport.BuildHonuaProxyUrl(
            CrossServerConsumeTestSupport.BuildUrl(baseUrl, parameters));
}
