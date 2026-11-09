// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    [UnitTheory]
    [InlineData("resources")]
    [InlineData("storageBindings")]
    [InlineData("publications")]
    public void BuildLinkedLayerMetadataV2Graph_OmittedStatus_InitializesReadyStatus(string collection)
    {
        var (source, scope, datasetId) = CreateCollidingStorageGraph(false);
        var document = JsonSerializer.SerializeToNode(source, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        document[collection]![0]!.AsObject().Remove("status");
        var graph = JsonSerializer.Deserialize(document.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var omitted = collection switch
        {
            "resources" => graph.Resources[0].Status,
            "storageBindings" => graph.StorageBindings[0].Status,
            _ => graph.Publications[0].Status
        };
        omitted.Should().BeNull();

        var linked = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph, "service-0", 1, "Dataset", 4326, DateTimeOffset.UnixEpoch, storageScope: scope);

        MetadataV2Status[] statuses =
        [
            linked.Resources.Single(dataset => dataset.Metadata.Id == datasetId).Status,
            linked.StorageBindings.Single(binding => binding.ResourceId == datasetId).Status,
            linked.Publications.Single(layer => layer.Metadata.Id == "pub-0").Status
        ];
        statuses.Should().AllSatisfy(status =>
        {
            status.Should().NotBeNull();
            status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
            status.State.Should().Be(MetadataV2OperationalState.Ready);
            status.ObservedAt.Should().Be(DateTimeOffset.UnixEpoch);
        });
    }
}
