// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Studio;

public sealed partial class PostgresStudioPackageStoreTests
{
    [IntegrationTest]
    public Task PublicationLineage_RepublishAndRollback_TracksOnlyDurablePublishedVersion()
        => WithLineageStoreAsync(async (store, schema) =>
        {
            var draft = await store.CreateDraftAsync(LineageDraft("service-a"));
            var first = await store.CreateVersionAsync(draft, null, "tester");
            await PublishLineageVersionAsync(store, first);
            var reader = new PostgresStudioPackageStore(new TestConnectionProvider(LineageFixture.DataSource, schema), schema);
            var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(reader);
            var forward = Assert.Single(await index.ListAppServicesAsync(draft.ItemId));
            Assert.Equal("service-a", forward.ServiceId);
            Assert.Equal(first.VersionId, forward.VersionId);
            Assert.Equal(forward, Assert.Single(await index.ListServiceAppsAsync("service-a")));
            var updated = await store.UpdateDraftAsync(draft with { Envelope = LineageEnvelope("service-b") });
            Assert.NotNull(updated);
            var second = await store.CreateVersionAsync(updated, null, "tester");
            Assert.Equal(first.VersionId, Assert.Single(await index.ListAppServicesAsync(draft.ItemId)).VersionId);
            await PublishLineageVersionAsync(store, second);
            Assert.Empty(await index.ListServiceAppsAsync("service-a"));
            Assert.Equal(second.VersionId, Assert.Single(await index.ListServiceAppsAsync("service-b")).VersionId);
            await store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Published, "tester", "restore");
            Assert.Empty(await index.ListServiceAppsAsync("service-b"));
            Assert.Equal(first.VersionId, Assert.Single(await index.ListServiceAppsAsync("service-a")).VersionId);
        });

    [IntegrationTest]
    public Task PublicationLineage_Unpublish_RemovesActiveEdgesAndRetainsImmutableDependencies()
        => WithLineageStoreAsync(async (store, schema) =>
        {
            var draft = await store.CreateDraftAsync(LineageDraft("service-a"));
            var version = await store.CreateVersionAsync(draft, null, "tester");
            await PublishLineageVersionAsync(store, version);
            var unpublisher = Assert.IsAssignableFrom<IStudioPackageUnpublisher>(store);
            var command = new StudioUnpublishCommand
            {
                RequestId = Guid.NewGuid(), ItemId = draft.ItemId, ExpectedPublishedVersionId = version.VersionId,
                TenantId = "tenant-a", ActorId = "tester", Reason = "withdraw"
            };
            var pointers = await unpublisher.UnpublishAsync(command);
            Assert.Null(pointers.PublishedVersionId);
            Assert.Equal(version.VersionId, pointers.CurrentVersionId);
            var reader = new PostgresStudioPackageStore(new TestConnectionProvider(LineageFixture.DataSource, schema), schema);
            var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(reader);
            Assert.Empty(await index.ListAppServicesAsync(draft.ItemId));
            Assert.Empty(await index.ListServiceAppsAsync("service-a"));
            Assert.Equal(1, await CountDependencyRowsAsync(schema, version.VersionId));
            Assert.Null(await reader.GetActivePublicationRequestByRouteAsync("/apps/lineage"));
            Assert.Equal(pointers, await Assert.IsAssignableFrom<IStudioPackageUnpublisher>(reader).UnpublishAsync(command));
        });

    [IntegrationTest]
    public Task PublicationLineage_StaleUnpublishApproval_LeavesNewPublicationAndDependenciesIntact()
        => WithLineageStoreAsync(async (store, schema) =>
        {
            var draft = await store.CreateDraftAsync(LineageDraft("service-a"));
            var first = await store.CreateVersionAsync(draft, null, "tester");
            await PublishLineageVersionAsync(store, first);
            var second = await store.CreateVersionAsync(draft, "second", "tester");
            await PublishLineageVersionAsync(store, second);
            var unpublisher = Assert.IsAssignableFrom<IStudioPackageUnpublisher>(store);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unpublisher.UnpublishAsync(new()
            {
                RequestId = Guid.NewGuid(), ItemId = draft.ItemId, ExpectedPublishedVersionId = first.VersionId,
                TenantId = "tenant-a", ActorId = "tester"
            }));
            Assert.Equal(second.VersionId, (await store.GetPointersAsync(draft.ItemId))!.PublishedVersionId);
            var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
            Assert.Equal(second.VersionId, Assert.Single(await index.ListServiceAppsAsync("service-a")).VersionId);
            Assert.Equal(1, await CountDependencyRowsAsync(schema, first.VersionId));
            Assert.Equal(1, await CountDependencyRowsAsync(schema, second.VersionId));
        });

    private async Task WithLineageStoreAsync(Func<PostgresStudioPackageStore, string, Task> verify)
    {
        var schema = await LineageFixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests) + "_Lineage");
        try
        {
            await EnsureStudioTablesAsync(schema);
            await verify(new PostgresStudioPackageStore(new TestConnectionProvider(LineageFixture.DataSource, schema), schema), schema);
        }
        finally
        {
            await LineageFixture.DropSchemaAsync(schema);
        }
    }

    private static Task<StudioPublicationRequest> PublishLineageVersionAsync(PostgresStudioPackageStore store, StudioContentVersion version)
        => store.CreatePublicationRequestAsync(new()
        {
            RequestId = Guid.NewGuid(), ItemId = version.ItemId, VersionId = version.VersionId,
            Status = StudioPublicationRequestStatus.Accepted, RequestedBy = "tester", CreatedAt = DateTimeOffset.UtcNow,
            Intent = new() { Route = "/apps/lineage", Visibility = "public" }
        }, version.VersionId);

    private static StudioPackageDraft LineageDraft(string serviceId) => BuildDraft("1=1", "lineage-app") with
    {
        Family = StudioPackageFamily.App, TenantId = "tenant-a", Envelope = LineageEnvelope(serviceId)
    };

    private static StudioPackageEnvelope LineageEnvelope(string serviceId) => new()
    {
        Family = StudioPackageFamily.App, SchemaVersion = "studio_app.v1", Format = "honua_app_package.v1",
        Dependencies = [new() { Kind = "service", Ref = serviceId }]
    };
}
