// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Xunit;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
public sealed class CapabilitiesMetadataXmlTests
{
    private const string ValidText = "Rivers & <lakes> \"survey\" '2026' 🌊\t\n";
    private const string InvalidText = ValidText + "before\u0001after\uFFFE";
    private const string SanitizedText = ValidText + "before\uFFFDafter\uFFFD";

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData("1.1.1", ValidText, ValidText)]
    [InlineData("1.1.1", InvalidText, SanitizedText)]
    [InlineData("1.3.0", ValidText, ValidText)]
    [InlineData("1.3.0", InvalidText, SanitizedText)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public Task Wms_GetCapabilities_MetadataProducesValidXml(string version, string input, string expected)
        => AssertCapabilitiesAsync(new CapabilitiesRequest(MetadataV2ServiceProtocols.Wms, MetadataV2LayerType.WmsLayer,
            $"/rest/services/capabilities_extents/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={version}"), input, expected);

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData("1.0.0", ValidText, ValidText)]
    [InlineData("1.0.0", InvalidText, SanitizedText)]
    [InlineData("1.1.0", ValidText, ValidText)]
    [InlineData("1.1.0", InvalidText, SanitizedText)]
    [InlineData("2.0.0", ValidText, ValidText)]
    [InlineData("2.0.0", InvalidText, SanitizedText)]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public Task Wfs_GetCapabilities_MetadataProducesValidXml(string version, string input, string expected)
        => AssertCapabilitiesAsync(new CapabilitiesRequest(MetadataV2ServiceProtocols.Wfs20, MetadataV2LayerType.WfsFeatureType,
            $"/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION={version}"), input, expected);

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData("1.0.0", ValidText, ValidText)]
    [InlineData("1.0.0", InvalidText, SanitizedText)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    public Task Wmts_GetCapabilities_MetadataProducesValidXml(string version, string input, string expected)
        => AssertCapabilitiesAsync(new CapabilitiesRequest(MetadataV2ServiceProtocols.Wmts, MetadataV2LayerType.WmtsLayer,
            $"/rest/services/capabilities_extents/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION={version}"), input, expected);

    private static async Task AssertCapabilitiesAsync(
        CapabilitiesRequest request, string input, string expected)
    {
        var provider = CapabilitiesExtentFixture.PublishedLayers(request.Protocol, request.LayerType);
        await SetMetadataAsync(provider, input);
        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(provider)
            .ReplaceService<ICoordinateTransformService>(new CapabilitiesExtentFixture.ExtentTransformProbe(false));
        await fixture.InitializeAsync();

        using var response = await fixture.Client.GetAsync(request.Url);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        var layers = XDocument.Parse(content).Descendants()
            .Where(element => element.Name.LocalName is "Layer" or "FeatureType")
            .Where(element => element.Elements().Any(child => child.Name.LocalName is "Name" or "Identifier"))
            .ToArray();
        Assert.Equal(2, layers.Length);
        Assert.All(layers, layer =>
        {
            Assert.Equal(expected, layer.Elements().Single(child => child.Name.LocalName == "Title").Value);
            if (request.LayerType != MetadataV2LayerType.WmtsLayer)
            {
                Assert.Equal(expected, layer.Elements().Single(child => child.Name.LocalName == "Abstract").Value);
            }
        });
    }

    private sealed record CapabilitiesRequest(
        string Protocol, MetadataV2LayerType LayerType, string Url);

    private static async Task SetMetadataAsync(TestMetadataV2GraphProvider provider, string input)
    {
        var graph = (await provider.GetCurrentAsync()).Graph;
        provider.SetGraph(graph with
        {
            Services = graph.Services.Select(service => service with
            {
                Metadata = service.Metadata with { Title = input, Description = input }
            }).ToArray(),
            Resources = graph.Resources.Select(resource => resource with
            {
                Metadata = resource.Metadata with { Title = input, Description = input }
            }).ToArray()
        });
    }
}
