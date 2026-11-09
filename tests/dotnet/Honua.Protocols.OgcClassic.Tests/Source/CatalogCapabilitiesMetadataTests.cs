// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
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
        identification.Elements().Single(element => element.Name.LocalName == "AccessConstraints").Value.Should().Be("MIT");
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
        var metadataUrl = layer.Elements().Single(element => element.Name.LocalName == "MetadataURL");
        metadataUrl.Descendants().Single(element => element.Name.LocalName == "OnlineResource")
            .Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))!.Value.Should().Be("https://example.com/catalog/roads");
        text.Should().Contain("/ogc/services/test/wms").And.NotContain("/MapServer/WMS");
    }

    private void SeedMetadata()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        MetadataV2ObjectMetadata Fields(MetadataV2ObjectMetadata metadata, string title) => metadata with
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
            Metadata = Fields(snapshot.Graph.Metadata, "Global survey"),
            Services = snapshot.Graph.Services.Select(service => service with
            {
                Metadata = Fields(service.Metadata, "Service survey")
            }).ToArray()
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graph, schema: _fixture.CurrentSchema);
    }
}
