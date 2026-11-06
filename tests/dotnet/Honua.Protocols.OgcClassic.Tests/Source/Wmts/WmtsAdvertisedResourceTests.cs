// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class WmtsAdvertisedResourceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(null, "")]
    [InlineData("Public", "")]
    [InlineData(null, "/arcgis")]
    [InlineData("Public", "/arcgis")]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    public async Task GetCapabilities_NativeAlias_AdvertisesReachablePublicResources(string? folderName, string mount)
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var graph = original with
        {
            Folders = folderName is null ? [] : [new() { Metadata = new() { Id = "wmts-folder", Name = folderName } }],
            Services = original.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                ? service with { PublicName = "Parcels", FolderId = folderName is null ? null : "wmts-folder" }
                : service).ToArray()
        };
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph, schema: _fixture.CurrentSchema);
        try
        {
            var aliasPath = $"{mount}/ogc/services/{WebAppFixture.TestServiceId}/wmts";
            using var response = await _fixture.Client.GetAsync($"{aliasPath}?SERVICE=WMTS&REQUEST=GetCapabilities");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
            XNamespace wmts = "http://www.opengis.net/wmts/1.0";
            XNamespace ows = "http://www.opengis.net/ows/1.1";
            XNamespace xlink = "http://www.w3.org/1999/xlink";
            var kvp = document.Descendants(ows + "Get").First(element => element.Descendants(ows + "Value")
                .Any(value => value.Value == "KVP"));
            new Uri(kvp.Attribute(xlink + "href")!.Value).AbsolutePath.Should().Be(aliasPath);
            var publicPath = folderName is null ? "Parcels" : $"{folderName}/Parcels";
            var metadataUrl = document.Descendants(wmts + "ServiceMetadataURL").Single().Attribute(xlink + "href")!.Value;
            new Uri(metadataUrl).AbsolutePath.Should().EndWith($"/rest/services/{publicPath}/MapServer/WMTS/1.0.0/WMTSCapabilities.xml");
            using var metadata = await _fixture.Client.GetAsync(new Uri(metadataUrl).PathAndQuery);
            metadata.StatusCode.Should().Be(HttpStatusCode.OK);
            XDocument.Parse(await metadata.Content.ReadAsStringAsync()).Root!.Name.Should().Be(wmts + "Capabilities");
            var tileResource = document.Descendants(wmts + "ResourceURL").First(element => element.Attribute("resourceType")?.Value == "tile");
            var tileUrl = tileResource.Attribute("template")!.Value.Replace("{Style}", "default", StringComparison.Ordinal)
                .Replace("{TileMatrixSet}", "WebMercatorQuad", StringComparison.Ordinal)
                .Replace("{TileMatrix}", "0", StringComparison.Ordinal).Replace("{TileRow}", "0", StringComparison.Ordinal)
                .Replace("{TileCol}", "0", StringComparison.Ordinal);
            foreach (var dimension in tileResource.Parent!.Elements(wmts + "Dimension"))
            {
                var identifier = dimension.Element(ows + "Identifier")!.Value;
                var defaultValue = dimension.Element(wmts + "Default")!.Value;
                tileUrl = tileUrl.Replace($"{{{identifier}}}", Uri.EscapeDataString(defaultValue), StringComparison.Ordinal);
            }
            using var tile = await _fixture.Client.GetAsync(new Uri(tileUrl).PathAndQuery);
            tile.StatusCode.Should().Be(HttpStatusCode.OK, await tile.Content.ReadAsStringAsync());
            tile.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            (await tile.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }
}
