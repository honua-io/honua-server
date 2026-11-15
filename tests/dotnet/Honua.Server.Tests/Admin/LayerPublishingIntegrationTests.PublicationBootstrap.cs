// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1)]
    [Operation(Operations.ContractTesting)]
    public async Task PublicationRevision_FirstSnapshotCreatedBeforeSave_RejectsWithoutPublishing(long revision)
    {
        await UseServerFeatureStoreConnectionAsync();
        var bootstrap = new MetadataV2GraphSnapshot(new MetadataV2Graph { Revision = revision }, "bootstrap", DateTimeOffset.UtcNow);
        var changed = new MetadataV2GraphSnapshot(bootstrap.Graph with { Revision = 1 }, "concurrent", DateTimeOffset.UtcNow);
        var store = Substitute.For<IMetadataV2GraphStore, IMetadataV2GraphWriteBaseReader>();
        var concurrentWrite = false;
        ((IMetadataV2GraphWriteBaseReader)store).TryGetPersistedCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(concurrentWrite ? changed : (MetadataV2GraphSnapshot?)null));
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        store.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<MetadataV2GraphSnapshot>(bootstrap));
#pragma warning restore CA2012
        store.SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            concurrentWrite = true;
            _publishingFixture.SetGraph(changed.Graph);
            if (call.ArgAt<string?>(1) is not null)
            {
                throw new MetadataV2GraphConcurrencyException("Concurrent first snapshot", "bootstrap", changed.Etag);
            }
            _publishingFixture.SetGraph(call.ArgAt<MetadataV2Graph>(0));
            return Task.FromResult(new MetadataV2GraphSnapshot(call.ArgAt<MetadataV2Graph>(0), "overwritten", DateTimeOffset.UtcNow));
        });
        var publish = () => PublishRevisionPinnedAsync(RevisionPinnedPublication(revision, "bootstrap"), store);

        var rejected = (await publish.Should().ThrowAsync<LayerPublishingException>()).Which;
        rejected.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict, rejected.Message);
        concurrentWrite.Should().BeTrue();
        (await GetPublishedLayerRowCountAsync()).Should().Be(0);
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(changed.Graph);
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task PublicationRevision_FirstSnapshotBeforeGraphLoadWithSameRevision_RejectsWithoutPublishing()
    {
        await UseServerFeatureStoreConnectionAsync();
        var changed = new MetadataV2GraphSnapshot(new MetadataV2Graph { Revision = 1 }, "concurrent", DateTimeOffset.UtcNow);
        var store = Substitute.For<IMetadataV2GraphStore, IMetadataV2GraphWriteBaseReader>();
        ((IMetadataV2GraphWriteBaseReader)store).TryGetPersistedCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MetadataV2GraphSnapshot?>(changed));
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        store.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<MetadataV2GraphSnapshot>(changed));
#pragma warning restore CA2012
        store.SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new MetadataV2GraphSnapshot(call.ArgAt<MetadataV2Graph>(0), "saved", DateTimeOffset.UtcNow)));
        var request = RevisionPinnedPublication(1, "bootstrap");
        var publish = () => PublishRevisionPinnedAsync(request, store);

        var rejected = (await publish.Should().ThrowAsync<LayerPublishingException>()).Which;
        rejected.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict, rejected.Message);
        await store.DidNotReceive().SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await GetPublishedLayerRowCountAsync()).Should().Be(0);
    }

    private static PostgreSqlLayerPublishingService CreateRevisionPublisher(IServiceProvider services, IMetadataV2GraphStore store) =>
        new PostgreSqlLayerPublishingService(
            services.GetRequiredService<ITableDiscoveryService>(), store,
            services.GetRequiredService<ILogger<PostgreSqlLayerPublishingService>>(),
            services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Database:Schema"],
            services.GetService<Honua.Core.Features.Styling.Abstractions.IStyleCatalog>(),
            services.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
            services.GetRequiredService<IOptions<LayerPublishingOptions>>().Value,
            services.GetService<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>(),
            services.GetRequiredService<Honua.Db.Postgres.Features.Infrastructure.PostgresSchemaConfiguration>(),
            services.GetService<ISchemaContext>(),
            services.GetRequiredService<IOptions<Honua.Db.Postgres.Features.Infrastructure.PostgresSessionLockConnections>>());
}
