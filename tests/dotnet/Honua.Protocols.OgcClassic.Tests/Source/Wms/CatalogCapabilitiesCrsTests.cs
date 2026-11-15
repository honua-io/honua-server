// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
[Operation(Operations.Metadata)]
public sealed class CatalogCapabilitiesCrsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("1.3.0", "CRS:84", "CRS")]
    [InlineData("1.1.1", "EPSG:4326", "SRS")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    public async Task WmsCapabilities_Crs84Only_PublishesVersionCompatibleCrs(string version, string expectedCrs, string crsElement)
    {
        _fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Title = "CRS84 roads" });
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1];
        _fixture.UpdateV2ResourceMetadata(1, spatial: resource.Spatial! with
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Bbox = new MetadataV2Bbox { West = -123, South = 37, East = -122, North = 38 },
            SupportedCrs = [new MetadataV2SpatialReference { Srid = 4326, Crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84" }]
        });
        using var response = await _fixture.Client.GetAsync($"/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={version}");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var document = XDocument.Parse(text);
        var layer = document.Descendants().Single(element => element.Name.LocalName == "Layer" &&
            element.Elements().Any(child => child.Name.LocalName == "Title" && child.Value == "CRS84 roads"));
        layer.Elements().Where(element => element.Name.LocalName == crsElement)
            .Select(element => element.Value).Should().Equal(expectedCrs);
        var bounds = layer.Elements().Single(element => element.Name.LocalName == "BoundingBox");
        bounds.Attribute(crsElement)!.Value.Should().Be(expectedCrs);
        double.Parse(bounds.Attribute("minx")!.Value, CultureInfo.InvariantCulture).Should().BeApproximately(-123, 0.001);
        double.Parse(bounds.Attribute("miny")!.Value, CultureInfo.InvariantCulture).Should().BeApproximately(37, 0.001);
    }

    [IntegrationTheory]
    [InlineData("1.3.0", 37, -123)]
    [InlineData("1.1.1", -123, 37)]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    public async Task WmsCapabilities_GeographicCrs_BoundingBoxMatchesVersionAxisOrder(string version, double minX, double minY)
    {
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1];
        _fixture.UpdateV2ResourceMetadata(1, spatial: resource.Spatial! with
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Bbox = new MetadataV2Bbox { West = -123, South = 37, East = -122, North = 38 },
            SupportedCrs = [new MetadataV2SpatialReference { Srid = 4258, Crs = "EPSG:4258" }]
        });
        using var response = await _fixture.Client.GetAsync($"/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={version}");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var document = XDocument.Parse(text);
        var bounds = document.Descendants().Single(element => element.Name.LocalName == "BoundingBox" &&
            element.Attributes().Any(attribute => attribute.Name.LocalName is "CRS" or "SRS" && attribute.Value == "EPSG:4258"));

        double.Parse(bounds.Attribute("minx")!.Value, CultureInfo.InvariantCulture).Should().BeApproximately(minX, 0.001);
        double.Parse(bounds.Attribute("miny")!.Value, CultureInfo.InvariantCulture).Should().BeApproximately(minY, 0.001);
    }

    [IntegrationTheory]
    [InlineData("1.3.0", "CRS")]
    [InlineData("1.1.1", "SRS")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    public async Task WmsCapabilities_DefaultCrs_RootDeclaresCommonChildCrs(string version, string elementName)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources.Select(resource => resource with
            {
                Spatial = resource.Spatial is null ? null : resource.Spatial with { SupportedCrs = [] }
            }).ToArray()
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graph, schema: _fixture.CurrentSchema);
        using var response = await _fixture.Client.GetAsync($"/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={version}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var rootLayer = document.Descendants().First(element => element.Name.LocalName == "Layer");
        var declared = rootLayer.Elements().Where(element => element.Name.LocalName == elementName).Select(element => element.Value);
        declared.Should().Contain("EPSG:4326").And.Contain("EPSG:3857");
        if (version == "1.3.0")
            declared.Should().Contain("CRS:84");
    }

    [IntegrationTheory]
    [InlineData("1.3.0", "CRS", "CRS:84")]
    [InlineData("1.1.1", "SRS", "EPSG:4326")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    public async Task WmsCapabilities_Crs84UriAndInvalidReference_AdvertisesOnlyValidVersionCrs(string version, string elementName, string expected)
    {
        _fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Title = "CRS normalization" });
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1];
        _fixture.UpdateV2ResourceMetadata(1, spatial: resource.Spatial! with
        {
            SupportedCrs = [new() { Crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84" }, new() { Srid = 4326 }, new()]
        });
        using var response = await _fixture.Client.GetAsync($"/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={version}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var layer = document.Descendants().Single(element => element.Name.LocalName == "Layer" &&
            element.Elements().Any(child => child.Name.LocalName == "Title" && child.Value == "CRS normalization"));
        var declared = layer.Elements().Where(element => element.Name.LocalName == elementName).Select(element => element.Value).ToArray();
        declared.Should().Contain(expected).And.NotContain("EPSG:");
        declared.Should().HaveCount(version == "1.3.0" ? 2 : 1);
    }

    [IntegrationTheory]
    [InlineData("1.3.0", "CRS")]
    [InlineData("1.1.1", "SRS")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    public async Task WmsCapabilities_DisjointChildCrs_RootDeclaresNoCommonCrs(string version, string elementName)
    {
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1];
        _fixture.UpdateV2ResourceMetadata(1, spatial: resource.Spatial! with
        {
            SupportedCrs = [new() { Srid = 26910, Crs = "EPSG:26910" }]
        });
        using var response = await _fixture.Client.GetAsync($"/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={version}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var rootLayer = document.Descendants().First(element => element.Name.LocalName == "Layer");
        var declared = rootLayer.Elements().Where(element => element.Name.LocalName == elementName).ToArray();
        if (version == "1.1.1")
            declared.Should().ContainSingle().Which.Value.Should().BeEmpty();
        else
            declared.Should().BeEmpty();
    }

}
