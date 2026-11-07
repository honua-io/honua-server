// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2CollectionIdsTests
{
    [UnitTest]
    public void ForMaps_AliasSharedAcrossServices_UsesPublicationSpecificIds()
    {
        var graph = Graph();
        graph = graph with
        {
            Services = [graph.Services[0], graph.Services[0] with { Metadata = new() { Id = "other-service", Name = "other" } }],
            Publications = [graph.Publications[0], graph.Publications[0] with
            { Metadata = new() { Id = "other-publication", Name = "other" }, ServiceId = "other-service" }]
        };
        var ids = MetadataV2CollectionIds.ForMaps(new(graph, "test", DateTimeOffset.UtcNow));
        Assert.Equal("publication", ids["publication"]);
        Assert.Equal("other-publication", ids["other-publication"]);
    }

    [UnitTest]
    public void ForMaps_DuplicatePublicationId_UsesCanonicalFirstPublication()
    {
        var graph = Graph();
        graph = graph with { Publications = [graph.Publications[0], graph.Publications[0] with { ServiceLocalId = "duplicate-alias" }] };
        var ids = MetadataV2CollectionIds.ForMaps(new(graph, "test", DateTimeOffset.UtcNow));
        Assert.Equal("parcels", Assert.Single(ids).Value);
    }

    [UnitTest]
    public void ForMaps_AliasShadowsAnUnrelatedPublicationId_UsesItsOwnPublicationId()
    {
        var graph = Graph();
        graph = graph with
        {
            Publications = [graph.Publications[0], graph.Publications[0] with
            { Metadata = new() { Id = "parcels", Name = "different" }, ServiceLocalId = "different" }]
        };
        var ids = MetadataV2CollectionIds.ForMaps(new(graph, "test", DateTimeOffset.UtcNow));
        Assert.Equal("publication", ids["publication"]);
    }

    private static MetadataV2Graph Graph() => new()
    {
        Services = [new() { Metadata = new() { Id = "service", Name = "service" }, Protocols = [ServiceProtocols.OgcApiMaps] }],
        Resources = [new() { Metadata = new() { Id = "resource", Name = "parcels" } }],
        Publications = [new()
        {
            Metadata = new() { Id = "publication", Name = "parcels" }, ServiceId = "service", ResourceId = "resource", ServiceLocalId = "parcels"
        }]
    };
}
