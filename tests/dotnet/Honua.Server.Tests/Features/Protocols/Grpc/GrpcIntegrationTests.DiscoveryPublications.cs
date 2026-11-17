// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_PairedPublications_ReturnsEachNativeLayerOnce(bool stacFirst)
    {
        var baseline = await _client!.DescribeServiceAsync(new Proto.DescribeServiceRequest
        {
            ServiceId = WebAppFixture.TestServiceId
        }, _headers);
        baseline.Layers.Should().NotBeEmpty();
        AddPairedDiscoveryPublications(stacFirst);

        var response = await _client.DescribeServiceAsync(new Proto.DescribeServiceRequest
        {
            ServiceId = WebAppFixture.TestServiceId
        }, _headers);

        response.Layers.Select(layer => layer.LayerId)
            .Should().Equal(baseline.Layers.Select(layer => layer.LayerId));
        response.Service.Capabilities.Should().Be(baseline.Service.Capabilities);
        response.NextPageToken.Should().BeEmpty();
        var pagedLayers = await ReadDiscoveryPagesAsync(baseline.Layers.Count);
        pagedLayers.Should().Equal(response.Layers);
        await AssertDiscoveryMatchesLayerMetadataAsync(pagedLayers);
    }

    private void AddPairedDiscoveryPublications(bool stacFirst)
    {
        var serviceId = _fixture.GetCurrentV2GraphSnapshot().Graph.Services.Single(service =>
            service.Metadata.Name == WebAppFixture.TestServiceId &&
            service.Protocols.Contains(ServiceProtocols.Grpc, StringComparer.OrdinalIgnoreCase)).Metadata.Id;
        _fixture.MutateV2Graph(graph => graph with
        {
            Publications = graph.Publications.SelectMany(publication =>
            {
                if (publication.ServiceId != serviceId || !publication.LayerIndex.HasValue)
                {
                    return new[] { publication };
                }

                var stac = publication with
                {
                    Metadata = publication.Metadata with { Id = $"stac-{publication.Metadata.Id}" },
                    PublicationType = MetadataV2LayerType.StacCollection,
                    IsPrimary = false
                };
                return stacFirst ? new[] { stac, publication } : new[] { publication, stac };
            }).ToArray()
        });
    }

    private async Task AssertDiscoveryMatchesLayerMetadataAsync(IEnumerable<Proto.LayerSummary> summaries)
    {
        foreach (var summary in summaries)
        {
            var described = await _client!.DescribeLayerAsync(new Proto.DescribeLayerRequest
            {
                ServiceId = WebAppFixture.TestServiceId,
                LayerId = summary.LayerId
            }, _headers);
            described.Layer.LayerId.Should().Be(summary.LayerId);
            described.Layer.Name.Should().Be(summary.Name);
            described.Layer.GeometryType.Should().Be(summary.GeometryType);
        }
    }

    private async Task<List<Proto.LayerSummary>> ReadDiscoveryPagesAsync(int expectedCount)
    {
        var layers = new List<Proto.LayerSummary>();
        var pageToken = string.Empty;
        for (var index = 0; index < expectedCount; index++)
        {
            var page = await _client!.DescribeServiceAsync(new Proto.DescribeServiceRequest
            {
                ServiceId = WebAppFixture.TestServiceId,
                PageSize = 1,
                PageToken = pageToken
            }, _headers);
            page.Layers.Should().ContainSingle();
            layers.Add(page.Layers.Single());
            pageToken = page.NextPageToken;
            if (index < expectedCount - 1)
            {
                pageToken.Should().NotBeEmpty();
            }
        }

        pageToken.Should().BeEmpty();
        layers.Select(layer => layer.LayerId).Should().OnlyHaveUniqueItems();
        return layers;
    }
}
