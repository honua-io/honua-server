// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FolderProtocolEndpointTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    public static TheoryData<string, string, string> Endpoints
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            foreach (var mount in new[] { "", "/arcgis" })
                foreach (var (path, root) in new[]
                {
                ("/rest/services/Public/Parcels/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0", "WMS_Capabilities"),
                ("/rest/services/Public/Parcels/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0", "Capabilities"),
                ("/rest/services/Public/Parcels/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1", "Capabilities"),
                ("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0", "WFS_Capabilities"),
                ("/ogc/features", "links"),
                ("/rest/services/Public/Parcels/FeatureServer?f=json", "layers")
            })
                    cases.Add(mount, path, root);
            return cases;
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Endpoints))]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMS")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMTS")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/ImageServer/WCS")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer")]
    [Endpoint("GET /wfs")]
    [Endpoint("GET /ogc/features")]
    public async Task PublicEndpoint_FolderPublication_ResolvesAtBothMounts(string mount, string path, string root)
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        FolderServiceTestCatalog.ReplaceGraph(fixture, PublicProtocolGraph(original));
        try
        {
            using var response = await fixture.Client.GetAsync(mount + path);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            if (root is "links" or "layers")
            {
                using var json = JsonDocument.Parse(body);
                json.RootElement.GetProperty(root).ValueKind.Should().Be(JsonValueKind.Array);
            }
            else
            {
                XDocument.Parse(body).Root!.Name.LocalName.Should().Be(root);
            }
        }
        finally
        {
            FolderServiceTestCatalog.ReplaceGraph(fixture, original);
        }
    }

    [IntegrationTheory]
    [InlineData("")]
    [InlineData("/arcgis")]
    [Operation(Operations.Render)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task GetMap_FolderPublicEndpoint_RendersTheSameLayerAsTheNativeEndpoint(string mount)
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        FolderServiceTestCatalog.ReplaceGraph(fixture, PublicProtocolGraph(original));
        try
        {
            var path = mount + "/rest/services/Public/Parcels/MapServer/WMS";
            using var capabilities = await fixture.Client.GetAsync(path + "?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
            capabilities.StatusCode.Should().Be(HttpStatusCode.OK);
            var document = XDocument.Parse(await capabilities.Content.ReadAsStringAsync());
            XNamespace wms = "http://www.opengis.net/wms";
            var layer = document.Descendants(wms + "Layer").Select(element => element.Element(wms + "Name")?.Value)
                .Last(name => !string.IsNullOrWhiteSpace(name));
            var query = "?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&CRS=EPSG:3857&BBOX=-20037508,-20037508,20037508,20037508&WIDTH=256&HEIGHT=256&FORMAT=image/png&STYLES=&LAYERS="
                + Uri.EscapeDataString(layer!);
            using var qualified = await fixture.Client.GetAsync(path + query);
            using var native = await fixture.Client.GetAsync($"{mount}/ogc/services/{WebAppFixture.TestServiceId}/wms" + query);
            qualified.StatusCode.Should().Be(HttpStatusCode.OK, await qualified.Content.ReadAsStringAsync());
            native.StatusCode.Should().Be(HttpStatusCode.OK, await native.Content.ReadAsStringAsync());
            qualified.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            var png = await qualified.Content.ReadAsByteArrayAsync();
            png.Take(8).Should().Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            png.Should().Equal(await native.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            FolderServiceTestCatalog.ReplaceGraph(fixture, original);
        }
    }

    private static MetadataV2Graph PublicProtocolGraph(MetadataV2Graph graph)
    {
        var organized = FolderServiceTestCatalog.WithPublicParcels(graph);
        return organized with
        {
            Services = organized.Services.Select(service => service.Metadata.Id == "svc-image-test"
                ? service with { Protocols = [ServiceProtocols.ImageServer, ServiceProtocols.Wcs] }
                : service.Metadata.Id == "svc-test" ? service with
                {
                    Protocols = service.Protocols.Where(protocol => protocol != ServiceProtocols.Wcs).ToArray()
                } : service).ToArray()
        };
    }
}
