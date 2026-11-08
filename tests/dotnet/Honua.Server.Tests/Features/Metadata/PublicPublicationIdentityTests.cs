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

namespace Honua.Server.Tests.Features.Metadata;

[Collection("Database")]
public sealed class PublicPublicationIdentityTests : IAsyncLifetime
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
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task WmsCapabilities_StorageRebound_KeepsServiceLocalIdentifier()
    {
        var before = await WmsNamesAsync();
        RebindStorage();
        var after = await WmsNamesAsync();

        before.Should().Equal("0");
        after.Should().Equal("0");
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

        var expected = new[] { $"layer:{PublicationId}", $"layer:{SecondPublicationId}" };
        before.Should().BeEquivalentTo(expected);
        after.Should().BeEquivalentTo(expected);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/styles/{layerId}.json")]
    public async Task Style_StorageRebound_KeepsPublicationUrlAndResolvesCurrentStorage()
    {
        var path = $"/api/styles/{Uri.EscapeDataString(PublicationId)}.json";
        using var before = await _fixture.Client.GetAsync(path);
        var beforeBody = await before.Content.ReadAsStringAsync();
        RebindStorage();
        using var after = await _fixture.Client.GetAsync(path);
        var afterBody = await after.Content.ReadAsStringAsync();

        before.StatusCode.Should().Be(HttpStatusCode.OK, beforeBody);
        after.StatusCode.Should().Be(HttpStatusCode.OK, afterBody);
        using var beforeStyle = JsonDocument.Parse(beforeBody);
        using var afterStyle = JsonDocument.Parse(afterBody);
        beforeStyle.RootElement.GetProperty("name").GetString().Should().Be("publication-style");
        afterStyle.RootElement.GetProperty("name").GetString().Should().Be("publication-style");
        await _styles.Received().GetStyleAsync(Arg.Any<MetadataV2Resource>(), OriginalStorageLayerId, Arg.Any<CancellationToken>());
        await _styles.Received().GetStyleAsync(Arg.Any<MetadataV2Resource>(), ReboundStorageLayerId, Arg.Any<CancellationToken>());
        using var storageAlias = await _fixture.Client.GetAsync($"/api/styles/{OriginalStorageLayerId}.json");
        storageAlias.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    private async Task<string[]> WmsNamesAsync()
    {
        var xml = await ReadXmlAsync($"/ogc/services/{ServiceName}/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
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
