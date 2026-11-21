// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

[Protocol(ProtocolNames.TestQuality)]
public sealed class ManagedWriteStorageTests
{
    private const string Graph = """
        {"services":[{"metadata":{"id":"svc","name":"svc"},"options":{"capabilities":["Query","Create","Update","Delete","Editing"]}}],
         "connections":[{"metadata":{"id":"conn"},"type":"managed"}],
         "resources":[{"metadata":{"id":"src","name":"src"},"storageBindingIds":["b-src"]},
                      {"metadata":{"id":"man","name":"man"},"storageBindingIds":["b-man"]}],
         "storageBindings":[
           {"metadata":{"id":"b-src"},"resourceId":"src","connectionId":"conn","storageType":"relational-table",
            "locator":"honua_data.imported_points","storageLayerId":7,"options":{"sourceBacked":true}},
           {"metadata":{"id":"b-man"},"resourceId":"man","storageType":"relational-table","locator":"features",
            "storageLayerId":8,"options":{"attributesColumn":"attributes","layerDiscriminatorColumn":"layer_id"}}],
         "publications":[{"metadata":{"id":"p-src"},"resourceId":"src","serviceId":"svc"},
                         {"metadata":{"id":"p-man"},"resourceId":"man","serviceId":"svc"},
                         {"metadata":{"id":"p-none"},"resourceId":"none","serviceId":"svc"}]}
        """;

    [UnitTest]
    [Operation(Operations.Create)]
    public void SourceTableOf_NamesOnlyTheSourceBackedPublication()
    {
        var snapshot = Snapshot();

        ManagedWriteStorage.SourceTableOf(snapshot, Publication(snapshot, "p-src")).Should().Be("honua_data.imported_points");
        ManagedWriteStorage.SourceTableOf(snapshot, Publication(snapshot, "p-man")).Should().BeNull();
        ManagedWriteStorage.SourceTableOf(snapshot, Publication(snapshot, "p-none")).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void WithoutUnservableEdits_SourceBackedPublication_KeepsOnlyReadCapabilities()
    {
        var snapshot = Snapshot();
        var service = snapshot.Graph.Services.Single();

        var advertised = ManagedWriteStorage.WithoutUnservableEdits(snapshot, service, Publication(snapshot, "p-src"));

        advertised.Capabilities.Should().Equal("Query");
        MetadataV2EditCapabilities.Supports(service, advertised, MetadataV2EditCapabilities.Create).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void WithoutUnservableEdits_ManagedPublication_IsUnchanged()
    {
        var snapshot = Snapshot();
        var publication = Publication(snapshot, "p-man");

        ManagedWriteStorage.WithoutUnservableEdits(snapshot, snapshot.Graph.Services.Single(), publication)
            .Should().BeSameAs(publication);
    }

    private static MetadataV2Layer Publication(MetadataV2GraphSnapshot snapshot, string id)
        => snapshot.Graph.Publications.Single(publication => publication.Metadata.Id == id);

    private static MetadataV2GraphSnapshot Snapshot()
        => new(JsonSerializer.Deserialize(JsonNode.Parse(Graph)!.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!,
            "etag", DateTimeOffset.UnixEpoch);
}
