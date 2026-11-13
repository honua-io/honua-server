// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols;

public sealed partial class PublicPublicationIdentityTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task RecordsService_ProtocolRowsShareResource_EveryLayerIdResolvesToRecord()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services.Select(service => service with
            {
                Metadata = service.Metadata with { Name = ServiceName },
            }).ToArray(),
        });

        var layerIds = await ServiceRecordLayerIdsAsync();

        layerIds.Should().Equal(PublicationId);
        foreach (var publicationId in layerIds)
        {
            using var response = await _fixture.Client.GetAsync(
                $"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString($"layer:{publicationId}")}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

}
