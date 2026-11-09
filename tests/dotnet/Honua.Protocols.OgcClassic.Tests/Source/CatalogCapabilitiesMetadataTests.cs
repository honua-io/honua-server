// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
[Operation(Operations.Metadata)]
public sealed class CatalogCapabilitiesMetadataTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0", "Service survey")]
    [InlineData("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.1.1", "Service survey")]
    [InlineData("/ogc/services/test/wmts?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0", "Service survey")]
    [InlineData("/ogc/services/test/wcs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1", "Service survey")]
    [InlineData("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0", "Global survey")]
    [InlineData("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.1.0", "Global survey")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task GetCapabilities_CatalogMetadata_UsesAuthoredIdentificationAndContact(string path, string title)
    {
        SeedMetadata();
        using var response = await _fixture.Client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var document = XDocument.Parse(text);
        var identification = document.Root!.Elements().Single(element =>
            element.Name.LocalName is "Service" or "ServiceIdentification");
        identification.Elements().Single(element => element.Name.LocalName == "Title").Value.Should().Be(title);
        identification.Elements().Single(element => element.Name.LocalName == "Abstract").Value.Should().Be("Survey <abstract> & details");
        identification.Descendants().Where(element => element.Name.LocalName == "Keyword")
            .Select(element => element.Value).Should().Contain("roads & bridges");
        document.Descendants().Where(element => element.Name.LocalName is "ElectronicMailAddress" or "ContactElectronicMailAddress")
            .Select(element => element.Value).Should().Contain("ops@example.com");
        identification.Elements().Single(element => element.Name.LocalName == "Fees").Value.Should().Be("USD 5 per request");
        identification.Elements().Single(element => element.Name.LocalName == "AccessConstraints").Value.Should().Be("Registered users");
        text.Should().NotContain("1 Honua Way").And.NotContain("support@honua.local").And.NotContain("Honua Support");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task WmsCapabilities_MetadataLinkAndCrs_UseCatalogAndRequestedRoute()
    {
        SeedMetadata();
        _fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with
        {
            Title = "Survey roads",
            Links = [new MetadataV2Link { Rel = "metadata", Href = "https://example.com/catalog/roads", Type = "application/xml" }]
        });
        _fixture.UpdateV2ResourceMetadata(1, spatial: new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Point,
            PrimaryGeometryField = "shape",
            SupportedCrs = [new MetadataV2SpatialReference { Srid = 26910, Crs = "EPSG:26910" }]
        });
        using var response = await _fixture.Client.GetAsync("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var document = XDocument.Parse(text);
        var layer = document.Descendants().Single(element => element.Name.LocalName == "Layer" &&
            element.Elements().Any(child => child.Name.LocalName == "Title" && child.Value == "Survey roads"));
        layer.Elements().Where(element => element.Name.LocalName == "CRS").Select(element => element.Value)
            .Should().Contain("EPSG:26910");
        var declaredCrs = layer.Elements().Where(element => element.Name.LocalName == "CRS").Select(element => element.Value).ToArray();
        declaredCrs.Should().Equal("EPSG:26910");
        foreach (var bounds in layer.Elements().Where(element => element.Name.LocalName == "BoundingBox"))
            declaredCrs.Should().Contain(bounds.Attribute("CRS")!.Value);
        var metadataUrl = layer.Elements().Single(element => element.Name.LocalName == "MetadataURL");
        metadataUrl.Descendants().Single(element => element.Name.LocalName == "OnlineResource")
            .Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))!.Value.Should().Be("https://example.com/catalog/roads");
        text.Should().Contain("/ogc/services/test/wms").And.NotContain("/MapServer/WMS");
    }

    [IntegrationTheory]
    [InlineData("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities")]
    [InlineData("/ogc/services/test/wmts?SERVICE=WMTS&REQUEST=GetCapabilities")]
    [InlineData("/ogc/services/test/wcs?SERVICE=WCS&REQUEST=GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    public async Task GetCapabilities_PartialFolderMetadata_InheritsGlobalContactAndStatements(string path)
    {
        SeedMetadata();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph with
        {
            Folders = [new() { Metadata = new() { Id = "survey-folder", Name = "survey", Title = "Folder survey" } }],
            Services = snapshot.Graph.Services.Select(service => service with
            {
                FolderId = "survey-folder", Metadata = new() { Id = service.Metadata.Id, Name = service.Metadata.Name }
            }).ToArray()
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);
        using var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var identification = document.Root!.Elements().Single(element => element.Name.LocalName is "Service" or "ServiceIdentification");
        identification.Elements().Single(element => element.Name.LocalName == "Title").Value.Should().Be("Folder survey");
        identification.Elements().Single(element => element.Name.LocalName == "Fees").Value.Should().Be("USD 5 per request");
        identification.Elements().Single(element => element.Name.LocalName == "AccessConstraints").Value.Should().Be("Registered users");
        document.Descendants().Where(element => element.Name.LocalName is "ElectronicMailAddress" or "ContactElectronicMailAddress")
            .Select(element => element.Value).Should().Contain("ops@example.com");
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs10Capabilities_AuthoredStatements_UsesGlobalIdentification()
    {
        SeedMetadata();
        using var response = await _fixture.Client.GetAsync("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.0.0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var service = document.Root!.Elements().Single(element => element.Name.LocalName == "Service");
        service.Elements().Single(element => element.Name.LocalName == "Title").Value.Should().Be("Global survey");
        service.Elements().Single(element => element.Name.LocalName == "Abstract").Value.Should().Be("Survey <abstract> & details");
        service.Elements().Single(element => element.Name.LocalName == "Fees").Value.Should().Be("USD 5 per request");
        service.Elements().Single(element => element.Name.LocalName == "AccessConstraints").Value.Should().Be("Registered users");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task WmsCapabilities_UntitledLayerWithoutMetadataLink_KeepsOwnNameAndOmitsMetadataUrl()
    {
        SeedMetadata();
        _fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Title = null, Links = [] });
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1];
        using var response = await _fixture.Client.GetAsync("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var layer = document.Descendants().Single(element => element.Name.LocalName == "Layer" &&
            element.Elements().Any(child => child.Name.LocalName == "Title" && child.Value == resource.Metadata.Name));
        layer.Elements().Where(element => element.Name.LocalName == "MetadataURL").Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities")]
    [InlineData("/ogc/services/test/wmts?SERVICE=WMTS&REQUEST=GetCapabilities")]
    [InlineData("/ogc/services/test/wcs?SERVICE=WCS&REQUEST=GetCapabilities")]
    [InlineData("/wfs?SERVICE=WFS&REQUEST=GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task GetCapabilities_NoPublishedResourcesOrStatements_ReturnsAnEmptyDocumentWithoutPlaceholders(string path)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph with
        {
            Metadata = new() { Id = snapshot.Graph.Metadata.Id, Name = snapshot.Graph.Metadata.Name },
            Folders = [], Resources = [], Publications = [], StorageBindings = [],
            Services = snapshot.Graph.Services.Select(service => service with
            {
                FolderId = null, Metadata = new() { Id = service.Metadata.Id, Name = service.Metadata.Name }
            }).ToArray()
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);
        using var response = await _fixture.Client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var document = XDocument.Parse(text);
        document.Root!.Name.LocalName.Should().BeOneOf("Capabilities", "WMS_Capabilities", "WFS_Capabilities");
        document.Descendants().Where(element => element.Name.LocalName is "Fees" or "AccessConstraints")
            .Should().OnlyContain(element => element.Value == string.Empty);
        text.Should().NotContain("1 Honua Way").And.NotContain("Honua Support").And.NotContain("support@honua.local");
    }

    private void SeedMetadata()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        MetadataV2ObjectMetadata WithAuthoredIdentification(MetadataV2ObjectMetadata metadata, string title) => metadata with
        {
            Title = title,
            Description = "Survey <abstract> & details",
            Keywords = ["roads & bridges"],
            License = "MIT",
            Publisher = "Survey agency",
            ContactPoint = new MetadataV2ContactPoint { Name = "Operations", Email = "ops@example.com", Url = "https://example.com/contact" }
        };
        var graph = snapshot.Graph with
        {
            Metadata = WithAuthoredIdentification(snapshot.Graph.Metadata, "Global survey"),
            Services = snapshot.Graph.Services.Select(service => service with
            {
                Metadata = WithAuthoredIdentification(service.Metadata, "Service survey")
            }).ToArray()
        };
        var json = JsonSerializer.SerializeToNode(graph, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        foreach (var metadata in json["services"]!.AsArray().Select(service => service!["metadata"]!).Prepend(json["metadata"]!))
        {
            metadata["fees"] = "USD 5 per request";
            metadata["accessConstraints"] = "Registered users";
        }
        var authored = JsonSerializer.Deserialize(json.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!;
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(authored, schema: _fixture.CurrentSchema);
    }
}
