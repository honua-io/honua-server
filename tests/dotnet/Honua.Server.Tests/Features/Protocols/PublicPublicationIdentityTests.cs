// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols;

[Collection("Database")]
public sealed partial class PublicPublicationIdentityTests : IAsyncLifetime
{
    private const string PublicationId = "roads:primary";
    private const string SecondPublicationId = "roads-secondary";
    private const string ServiceName = "public-roads";
    private const int OriginalStorageLayerId = 41;
    private const int ReboundStorageLayerId = 73;
    private readonly WebAppFixture _fixture = new();
    private readonly ILayerStyleService _styles = Substitute.For<ILayerStyleService>();

    public async Task InitializeAsync()
    {
        var style = JsonSerializer.Deserialize<JsonElement>(
            """{"version":8,"name":"publication-style","sources":{},"layers":[]}""");
        _styles.GetStyleAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new LayerStyleSnapshot(style, null));
        _fixture.ReplaceRequestService(_styles);
        await _fixture.InitializeAsync();
        ConfigurePublications();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [Endpoint("GET /wfs")]
    public async Task WfsCapabilities_StorageRebound_UsesDistinctEncodedPublicationIds()
    {
        var before = await WfsNamesAsync();
        RebindStorage();
        var after = await WfsNamesAsync();

        var expected = new[] { XmlConvert.EncodeLocalName(PublicationId), XmlConvert.EncodeLocalName(SecondPublicationId) };
        before.Should().BeEquivalentTo(expected);
        after.Should().BeEquivalentTo(expected);
        before.Select(XmlConvert.DecodeName).Should().BeEquivalentTo([PublicationId, SecondPublicationId]);
        foreach (var name in expected)
        {
            var schema = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0&TYPENAMES={Uri.EscapeDataString(name)}");
            XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
            schema.Root!.Elements(xsd + "element").Select(element => element.Attribute("name")!.Value).Should().Equal(name);
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task WmsCapabilities_StorageRebound_KeepsServiceLocalIdentifier()
    {
        var before = await WmsNamesAsync();
        var secondBefore = await WmsNamesAsync("second-roads");
        RebindStorage();
        var after = await WmsNamesAsync();
        var secondAfter = await WmsNamesAsync("second-roads");

        before.Should().Equal("0");
        after.Should().Equal("0");
        secondBefore.Should().Equal("0");
        secondAfter.Should().Equal("0");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task WmsCapabilities_LegacyPublication_UsesServiceLocalIdentifier()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Publications = graph.Publications.Select(publication => publication with
            {
                Identifier = new MetadataV2PublicationIdentifier(),
                ServiceLocalId = "0",
                LayerIndex = 0,
            }).ToArray(),
        });

        (await WmsNamesAsync()).Should().Equal("0");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task WmsCapabilities_ProtocolPublicationsShareResource_AdvertisesIdentifierOnce()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var primary = graph.Publications.Single(publication => publication.Metadata.Id == PublicationId);
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Publications = [.. graph.Publications, primary with
            {
                Metadata = primary.Metadata with { Id = "roads-map-view", Name = "roads-map-view" },
                PublicationType = MetadataV2PublicationType.EsriMapLayer,
                IsPrimary = false,
            }],
        });

        (await WmsNamesAsync()).Should().Equal("0");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Records_StorageRebound_KeepsPublicationRecordIds()
    {
        var before = await RecordIdsAsync();
        RebindStorage();
        var after = await RecordIdsAsync();

        var expected = new[] { $"layer:{PublicationId}" };
        before.Should().BeEquivalentTo(expected);
        after.Should().BeEquivalentTo(expected);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task RecordsService_StorageRebound_ListsStablePublicationIds()
    {
        var before = await ServiceRecordLayerIdsAsync();
        RebindStorage();
        var after = await ServiceRecordLayerIdsAsync();

        before.Should().Equal(PublicationId);
        after.Should().Equal(PublicationId);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [Endpoint("GET /wfs")]
    public async Task WfsCapabilities_EscapedPublicationId_DoesNotCollideWithLiteralEscape()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var ids = new[] { "a:b", "a_x003A_b" };
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Publications = graph.Publications.Select((publication, index) => publication with
            {
                Metadata = publication.Metadata with { Id = ids[index], Name = ids[index] },
            }).ToArray(),
        });

        var names = await WfsNamesAsync();
        names.Should().OnlyHaveUniqueItems();
        names.Select(XmlConvert.DecodeName).Should().BeEquivalentTo(ids);
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData(3)]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task Records_LegacyPublication_UsesOnlyNumericFeatureServerIndex(int? serviceLayerIndex)
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = graph.Publications.Single(candidate => candidate.Metadata.Id == PublicationId);
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services.Select(service => service.Metadata.Id == publication.ServiceId
                ? service with { Protocols = [.. service.Protocols, ServiceProtocols.FeatureServer] }
                : service).ToArray(),
            Publications = graph.Publications.Select(candidate => candidate.Metadata.Id == PublicationId
                ? candidate with { LayerIndex = serviceLayerIndex, ServiceLocalId = null, Identifier = new() }
                : candidate).ToArray(),
        });
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString($"layer:{PublicationId}")}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var record = JsonDocument.Parse(body);
        var featureServerLinks = record.RootElement.GetProperty("links").EnumerateArray()
            .Where(link => link.TryGetProperty("title", out var title) && title.GetString() == "FeatureServer layer").ToArray();
        if (serviceLayerIndex.HasValue)
        {
            featureServerLinks.Should().ContainSingle();
            featureServerLinks[0].GetProperty("href").GetString().Should().EndWith($"/FeatureServer/{serviceLayerIndex.Value}");
        }
        else
        {
            featureServerLinks.Should().BeEmpty();
        }
    }

    private async Task<string[]> WfsNamesAsync()
    {
        var xml = await ReadXmlAsync("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        XNamespace wfs = "http://www.opengis.net/wfs/2.0";
        return xml.Descendants(wfs + "FeatureType").Select(element =>
        {
            var name = element.Element(wfs + "Name")!.Value;
            return name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..];
        }).ToArray();
    }

    private async Task<string[]> WmsNamesAsync(string serviceName = ServiceName)
    {
        var xml = await ReadXmlAsync($"/ogc/services/{serviceName}/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
        XNamespace wms = "http://www.opengis.net/wms";
        return xml.Descendants(wms + "Layer")
            .Where(layer => !layer.Elements(wms + "Layer").Any())
            .Select(layer => layer.Element(wms + "Name")!.Value).ToArray();
    }

    private async Task<XDocument> ReadXmlAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body);
    }

    private async Task<string[]> RecordIdsAsync()
    {
        using var response = await _fixture.Client.GetAsync("/ogc/records/collections/honua-catalog/items?limit=100");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()!)
            .Where(id => id.StartsWith("layer:", StringComparison.Ordinal)).ToArray();
    }

    private async Task<string[]> ServiceRecordLayerIdsAsync()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/honua-catalog/items/service:{ServiceName}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("properties").GetProperty("layerIds").EnumerateArray()
            .Select(id => id.ToString()).ToArray();
    }

    private void ConfigurePublications()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            snapshot.ResolveStorageLayerId(candidate) == WebAppFixture.TestLayerId &&
            snapshot.ResolveResource(candidate)?.Type == MetadataV2ResourceType.FeatureDataset);
        var resource = snapshot.ResolveResource(publication)!;
        var binding = snapshot.ResolveStorageBinding(publication)!;
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var primary = publication with
        {
            Metadata = publication.Metadata with { Id = PublicationId, Name = PublicationId },
            LayerIndex = 0,
            Identifier = new MetadataV2PublicationIdentifier { Value = "0", IsNumeric = true },
            IsPrimary = true,
        };
        var secondService = service with
        {
            Metadata = service.Metadata with { Id = "second-service", Name = "second-roads" },
            Route = "second-roads",
            Protocols = [ServiceProtocols.Wfs20, ServiceProtocols.Wms],
        };
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = [resource with { Temporal = null }],
            StorageBindings = [binding with { StorageLayerId = OriginalStorageLayerId }],
            Services =
            [
                service with
                {
                    Metadata = service.Metadata with { Name = ServiceName },
                    Route = ServiceName,
                    Protocols = [ServiceProtocols.Wfs20, ServiceProtocols.Wms],
                },
                secondService,
            ],
            Publications = [primary, primary with
            {
                Metadata = primary.Metadata with { Id = SecondPublicationId, Name = SecondPublicationId },
                ServiceId = secondService.Metadata.Id,
                IsPrimary = false,
            }],
        };
        SetGraph(graph);
    }

    private void RebindStorage()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            StorageBindings = graph.StorageBindings.Select(binding => binding with { StorageLayerId = ReboundStorageLayerId }).ToArray(),
        });
    }

    private void SetGraph(MetadataV2Graph graph) => _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
        .SetGraph(graph, schema: _fixture.MetadataGraphSchema);
}
