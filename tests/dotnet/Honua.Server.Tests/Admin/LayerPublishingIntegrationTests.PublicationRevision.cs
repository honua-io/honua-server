// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task PublicationRevision_StalePreview_RejectsWithoutPublishing()
    {
        await UseServerFeatureStoreConnectionAsync();
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var request = RevisionPinnedPublication(graph.Revision);
        var changed = graph with { Revision = graph.Revision + 1 };
        _publishingFixture.SetGraph(changed);

        var publish = () => PublishRevisionPinnedAsync(request);

        (await publish.Should().ThrowAsync<LayerPublishingException>()).Which.ErrorKind
            .Should().Be(LayerPublishingErrorKind.Conflict);
        (await GetPublishedLayerRowCountAsync()).Should().Be(0);
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(changed);
    }

    [IntegrationTheory]
    [InlineData("matching")]
    [InlineData("legacy")]
    [Operation(Operations.ContractTesting)]
    public async Task PublicationRevision_CurrentOrUnpinned_Publishes(string mode)
    {
        await UseServerFeatureStoreConnectionAsync();
        var revision = _fixture.GetCurrentV2GraphSnapshot().Graph.Revision;
        var request = RevisionPinnedPublication(mode == "matching" ? revision : null);

        _layerId = (await PublishRevisionPinnedAsync(request)).StorageLayerId;

        (await GetPublishedLayerRowCountAsync()).Should().Be(1);
        // A direct service publish does not refresh the fixture's cached snapshot; read the persisted graph.
        using var scope = _fixture.Services.CreateScope();
        var persisted = await scope.ServiceProvider
            .GetRequiredService<Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore>().GetCurrentAsync();
        persisted.Graph.Revision.Should().Be(revision + 1);
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task PublicationRevision_ChangedDuringMaterialization_RejectsWithoutPublishing()
    {
        await UseServerFeatureStoreConnectionAsync();
        await InsertPostGisFeatureAsync("Second feature", 200, 2, 2);
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var changed = graph with { Revision = graph.Revision + 1 };
        var mutation = new ConcurrentPublicationMutation(() =>
        {
            _publishingFixture.SetGraph(changed);
        });
        using var progress = LayerSnapshotProgressScope.Begin(mutation);
        var publish = () => PublishRevisionPinnedAsync(RevisionPinnedPublication(graph.Revision));

        (await publish.Should().ThrowAsync<LayerPublishingException>()).Which.ErrorKind
            .Should().Be(LayerPublishingErrorKind.Conflict);
        mutation.Completed.Should().BeTrue();
        (await GetPublishedLayerRowCountAsync()).Should().Be(0);
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(changed);
    }

    private sealed class ConcurrentPublicationMutation(Action mutate) : ILayerSnapshotProgress
    {
        internal bool Completed { get; private set; }

        public Task ReportAsync(long rowsMaterialized, long rowsTotal, CancellationToken cancellationToken)
        {
            if (!Completed)
            {
                mutate();
                Completed = true;
            }
            return Task.CompletedTask;
        }
    }

    private LayerPublishRequest RevisionPinnedPublication(long? revision, string? etag = null) => new()
    {
        Schema = _schema,
        Table = _tableName,
        LayerName = $"Layer {_tableName}",
        ServiceName = _serviceName,
        GeometryColumn = "geom",
        GeometryType = "Point",
        Srid = 4326,
        PrimaryKey = "id",
        Fields = _idNamePopulationFields,
        ConnectionId = _connectionId,
        StorageMode = LayerStorageMode.Managed,
        ExpectedMetadataRevision = revision,
        ExpectedMetadataEtag = revision is null ? null : etag ?? _fixture.GetCurrentV2GraphSnapshot().Etag
    };

    private async Task<PublishedLayerSummary> PublishRevisionPinnedAsync(
        LayerPublishRequest request, Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var connectionString = await services.GetRequiredService<ISecureConnectionResolver>()
            .ResolveConnectionStringAsync(_connectionId);
        // The fixture's schema context already resolves to its current test schema.
        var publisher = graphStore is null
            ? services.GetRequiredService<ILayerPublishingService>()
            : CreateRevisionPublisher(services, graphStore);
        return await publisher.PublishLayerAsync(connectionString, request);
    }
}
