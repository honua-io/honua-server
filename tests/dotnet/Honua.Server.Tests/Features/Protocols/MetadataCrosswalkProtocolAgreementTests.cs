// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols;

// One published layer with full metadata describes itself the same way in every protocol: the publication's
// own title and description over the dataset's, the dataset's keywords, and governance inherited from the service and
// then the environment.
[Collection("Database")]
public sealed class MetadataCrosswalkProtocolAgreementTests : IAsyncLifetime
{
    private const string ServiceName = "crosswalk-trails";
    private const string PublicationId = "trails-feature";
    private const string StacPublicationId = "trails-stac";
    private const string EsriPublicationId = "trails-esri";
    private const string Title = "Park trails";
    private const string Description = "Every trail in the park, surveyed in 2026.";
    private const string License = "CC-BY-4.0";
    private const string Attribution = "Parks Department";
    private const string Contact = "Parks data desk";
    private static readonly XNamespace Ows11 = "http://www.opengis.net/ows/1.1";
    private static readonly XNamespace Wms = "http://www.opengis.net/wms";
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";
    private static readonly XNamespace Wmts = "http://www.opengis.net/wmts/1.0";

    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    }).WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.Client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
        ConfigureGraph();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Protocol(TestProtocols.Stac)]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    [Endpoint("GET /ogc/features/collections")]
    public async Task JsonCatalogues_DescribeTheLayerFromTheCrosswalk()
    {
        using var stac = await ReadJsonAsync("/stac/collections/1");
        using var record = await ReadJsonAsync($"/ogc/records/collections/honua-catalog/items/layer:{PublicationId}");
        using var collections = await ReadJsonAsync("/ogc/features/collections");
        var features = collections.RootElement.GetProperty("collections").EnumerateArray()
            .First(collection => collection.GetProperty("title").GetString() == Title);
        var recordProperties = record.RootElement.GetProperty("properties");

        foreach (var described in new[] { stac.RootElement, recordProperties, features })
        {
            described.GetProperty("title").GetString().Should().Be(Title);
            described.GetProperty("description").GetString().Should().Be(Description);
            Strings(described.GetProperty("keywords")).Should().Contain(["trails", "recreation"]);
        }

        stac.RootElement.GetProperty("license").GetString().Should().Be(License);
        recordProperties.GetProperty("license").GetString().Should().Be(License);
        features.GetProperty("attribution").GetString().Should().Be(Attribution);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiTiles)]
    [Protocol(TestProtocols.OgcApiMaps)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections")]
    [Endpoint("GET /ogc/maps/collections")]
    public async Task OgcApiTilesAndMaps_NameTheLayerFromTheCrosswalk()
    {
        using var tiles = await ReadJsonAsync("/ogc/tiles/collections");
        using var maps = await ReadJsonAsync("/ogc/maps/collections");

        foreach (var document in new[] { tiles, maps })
        {
            var collection = document.RootElement.GetProperty("collections").EnumerateArray()
                .FirstOrDefault(candidate => candidate.GetProperty("title").GetString() == Title);
            collection.ValueKind.Should().Be(JsonValueKind.Object, document.RootElement.GetRawText());
            Member(collection, "description").Should().Be(Description);
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Protocol(TestProtocols.Wfs20)]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [Endpoint("GET /wfs")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    public async Task OgcCapabilities_DescribeTheLayerFromTheCrosswalk()
    {
        var wms = (await ReadXmlAsync($"/ogc/services/{ServiceName}/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0"))
            .Descendants(Wms + "Layer").Single(layer => layer.Element(Wms + "Name")?.Value == "0");
        var wfs = (await ReadXmlAsync("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0"))
            .Descendants(Wfs + "FeatureType").Single(type => type.Element(Wfs + "Name")!.Value.Contains("trails-", StringComparison.Ordinal));
        var wmts = (await ReadXmlAsync($"/ogc/services/{ServiceName}/wmts?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0"))
            .Descendants(Wmts + "Layer").First();

        AssertLayerText(wms.Element(Wms + "Title"), wms.Element(Wms + "Abstract"), wms.Descendants(Wms + "Keyword"));
        AssertLayerText(wfs.Element(Wfs + "Title"), wfs.Element(Wfs + "Abstract"), wfs.Descendants(Ows11 + "Keyword"));
        AssertLayerText(wmts.Element(Ows11 + "Title"), wmts.Element(Ows11 + "Abstract"), wmts.Descendants(Ows11 + "Keyword"));
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Protocol(TestProtocols.MapServer)]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceName}/MapServer/{layerId}")]
    [Endpoint("GET /odata/Layers({layerId})")]
    public async Task GeoServicesAndOData_DescribeTheLayerFromTheCrosswalk()
    {
        using var featureLayer = await ReadJsonAsync($"/rest/services/{ServiceName}/FeatureServer/0?f=json");
        using var mapLayer = await ReadJsonAsync($"/rest/services/{ServiceName}/MapServer/0?f=json");
        using var odata = await ReadJsonAsync("/odata/Layers(0)");

        foreach (var layer in new[] { featureLayer.RootElement, mapLayer.RootElement })
        {
            Member(layer, "description").Should().Be(Description);
            Member(layer, "copyrightText").Should().Be(Attribution);
            Member(layer, "license").Should().Be(License, "the licence is inherited from the environment");
        }

        odata.RootElement.GetRawText().Should().Contain(Description);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Protocol(TestProtocols.Wfs20)]
    [Protocol(TestProtocols.FeatureServer)]
    [Protocol(TestProtocols.MapServer)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [Endpoint("GET /wfs")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceName}/MapServer")]
    public async Task ServiceDescriptions_InheritGovernanceThroughTheCrosswalk()
    {
        var wms = await ReadXmlAsync($"/ogc/services/{ServiceName}/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
        using var featureService = await ReadJsonAsync($"/rest/services/{ServiceName}/FeatureServer?f=json");
        using var mapService = await ReadJsonAsync($"/rest/services/{ServiceName}/MapServer?f=json");

        wms.Descendants(Wms + "AccessConstraints").Single().Value.Should().StartWith($"License: {License}");
        wms.Descendants(Wms + "ContactPerson").Single().Value.Should().Be(Contact);
        featureService.RootElement.GetProperty("license").GetString().Should().Be(License);
        featureService.RootElement.GetProperty("copyrightText").GetString().Should().Be(Attribution);
        mapService.RootElement.GetProperty("license").GetString().Should().Be(License);
        mapService.RootElement.GetProperty("documentInfo").GetProperty("Author").GetString().Should().Be(Contact);
    }

    private static void AssertLayerText(XElement? title, XElement? description, IEnumerable<XElement> keywords)
    {
        title!.Value.Should().Be(Title);
        description!.Value.Should().Be(Description);
        keywords.Select(keyword => keyword.Value).Should().Contain(["trails", "recreation"]);
    }

    private static string? Member(JsonElement element, string name)
    {
        element.TryGetProperty(name, out var value).Should().BeTrue($"{name} in {element.GetRawText()}");
        return value.GetString();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private async Task<JsonDocument> ReadJsonAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path}: {body}");
        var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse($"{path}: {body}");
        return document;
    }

    private async Task<XDocument> ReadXmlAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path}: {body}");
        return XDocument.Parse(body);
    }

    private void ConfigureGraph()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            snapshot.ResolveStorageLayerId(candidate) == WebAppFixture.TestLayerId &&
            snapshot.ResolveResource(candidate)?.Type == MetadataV2ObjectType.FeatureDataset);
        var resource = snapshot.ResolveResource(publication)!;
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var feature = publication with
        {
            Metadata = publication.Metadata with
            {
                Id = PublicationId,
                Name = PublicationId,
                Title = Title,
                Description = Description,
            },
            TitleOverride = null,
            LayerIndex = 0,
            Identifier = new MetadataV2LayerIdentifier { Value = "0", IsNumeric = true },
            IsPrimary = true,
        };
        SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Metadata = snapshot.Graph.Metadata with
            {
                License = License,
                Language = "en",
                ContactPoint = new MetadataV2ContactPoint { Name = Contact, Email = "parks@example.test" },
                Contacts = [],
            },
            Resources = [Described(resource)],
            Services = [Governed(service)],
            Publications = Surfaces(feature),
        });
    }

    // One publication per protocol surface, as a published service carries them.
    private static MetadataV2Layer[] Surfaces(MetadataV2Layer feature) =>
    [
        feature,
        feature with
        {
            Metadata = feature.Metadata with { Id = StacPublicationId, Name = StacPublicationId },
            PublicationType = MetadataV2LayerType.StacCollection,
            LayerIndex = 1,
            Identifier = new MetadataV2LayerIdentifier { Value = "1", IsNumeric = true },
            IsPrimary = false,
        },
        feature with
        {
            Metadata = feature.Metadata with { Id = EsriPublicationId, Name = EsriPublicationId },
            PublicationType = MetadataV2LayerType.EsriFeatureLayer,
            IsPrimary = false,
        },
    ];

    private static MetadataV2Object Described(MetadataV2Object resource) => resource with
    {
        Temporal = null,
        Metadata = resource.Metadata with
        {
            Title = "Trails dataset",
            Description = "The dataset's own description, which the publication overrides.",
            Keywords = ["trails", "recreation"],
            License = null,
            Attribution = null,
            ContactPoint = null,
            Contacts = [],
        },
    };

    private static MetadataV2Service Governed(MetadataV2Service service) => service with
    {
        Metadata = service.Metadata with
        {
            Name = ServiceName,
            Title = null,
            Description = null,
            License = null,
            Attribution = Attribution,
            ContactPoint = null,
            Contacts = [],
        },
        Route = ServiceName,
        FolderId = null,
        Protocols =
        [
            ServiceProtocols.FeatureServer, ServiceProtocols.MapServer, ServiceProtocols.OgcFeatures, ServiceProtocols.Wfs20,
            ServiceProtocols.Wms, ServiceProtocols.Wmts, ServiceProtocols.OData, ServiceProtocols.Stac,
            ServiceProtocols.OgcApiTiles, ServiceProtocols.OgcApiMaps,
        ],
    };

    private void SetGraph(MetadataV2Graph graph) => _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
        .SetGraph(graph, schema: _fixture.MetadataGraphSchema);
}
