// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioPublicationLineageTests
{
    [UnitTest]
    public async Task Publish_ActiveVersion_IndexesBothLineageDirections()
    {
        var store = new InMemoryStudioPackageStore();
        var draft = await store.CreateDraftAsync(Draft("service-a"));
        var version = await store.CreateVersionAsync(draft, null, "owner");
        var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
        Assert.Empty(await index.ListAppServicesAsync(draft.ItemId));
        await PublishAsync(store, version);
        var forward = Assert.Single(await index.ListAppServicesAsync(draft.ItemId));
        Assert.Equal("service-a", forward.ServiceId);
        Assert.Equal(version.VersionId, forward.VersionId);
        Assert.Equal("tenant-a", forward.TenantId);
        Assert.Equal(forward, Assert.Single(await index.ListServiceAppsAsync("service-a")));
    }

    [UnitTest]
    public async Task RepublishAndRollback_ReplacesActiveVersionEdges()
    {
        var store = new InMemoryStudioPackageStore();
        var draft = await store.CreateDraftAsync(Draft("service-a"));
        var first = await store.CreateVersionAsync(draft, null, "owner");
        await PublishAsync(store, first);
        var changed = await store.UpdateDraftAsync(draft with { Envelope = Envelope("service-b"), Generation = draft.Generation });
        Assert.NotNull(changed);
        var second = await store.CreateVersionAsync(changed, null, "owner");
        var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
        Assert.Equal(first.VersionId, Assert.Single(await index.ListAppServicesAsync(draft.ItemId)).VersionId);
        await PublishAsync(store, second);
        Assert.Empty(await index.ListServiceAppsAsync("service-a"));
        Assert.Equal(second.VersionId, Assert.Single(await index.ListServiceAppsAsync("service-b")).VersionId);
        await store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Published, "owner", "restore");
        Assert.Empty(await index.ListServiceAppsAsync("service-b"));
        Assert.Equal(first.VersionId, Assert.Single(await index.ListServiceAppsAsync("service-a")).VersionId);
    }

    [UnitTest]
    public async Task Unpublish_ExpectedActiveVersion_ClearsPointerAndBothLineageDirections()
    {
        var store = new InMemoryStudioPackageStore();
        var draft = await store.CreateDraftAsync(Draft("service-a"));
        var version = await store.CreateVersionAsync(draft, null, "owner");
        await PublishAsync(store, version);
        var unpublisher = Assert.IsAssignableFrom<IStudioPackageUnpublisher>(store);
        var command = new StudioUnpublishCommand
        {
            RequestId = Guid.NewGuid(), ItemId = draft.ItemId, ExpectedPublishedVersionId = version.VersionId,
            TenantId = "tenant-a", ActorId = "owner", Reason = "withdraw"
        };
        var pointers = await unpublisher.UnpublishAsync(command);
        Assert.Null(pointers.PublishedVersionId);
        Assert.Equal(version.VersionId, pointers.CurrentVersionId);
        var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
        Assert.Empty(await index.ListAppServicesAsync(draft.ItemId));
        Assert.Empty(await index.ListServiceAppsAsync("service-a"));
        Assert.Null(await store.GetActivePublicationRequestByRouteAsync("/apps/survey"));
        Assert.Equal(pointers, await unpublisher.UnpublishAsync(command));
    }

    [UnitTest]
    public async Task Unpublish_StaleApprovedVersion_LeavesPointerAndLineageUntouched()
    {
        var store = new InMemoryStudioPackageStore();
        var draft = await store.CreateDraftAsync(Draft("service-a"));
        var first = await store.CreateVersionAsync(draft, null, "owner");
        await PublishAsync(store, first);
        var second = await store.CreateVersionAsync(draft, "second", "owner");
        await PublishAsync(store, second);
        var unpublisher = Assert.IsAssignableFrom<IStudioPackageUnpublisher>(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unpublisher.UnpublishAsync(new()
        {
            RequestId = Guid.NewGuid(), ItemId = draft.ItemId, ExpectedPublishedVersionId = first.VersionId,
            TenantId = "tenant-a", ActorId = "owner"
        }));
        Assert.Equal(second.VersionId, (await store.GetPointersAsync(draft.ItemId))!.PublishedVersionId);
        var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
        Assert.Equal(second.VersionId, Assert.Single(await index.ListServiceAppsAsync("service-a")).VersionId);
    }

    private static Task<StudioPublicationRequest> PublishAsync(InMemoryStudioPackageStore store, StudioContentVersion version)
        => store.CreatePublicationRequestAsync(new()
        {
            RequestId = Guid.NewGuid(), ItemId = version.ItemId, VersionId = version.VersionId,
            Status = StudioPublicationRequestStatus.Accepted, RequestedBy = "owner", CreatedAt = DateTimeOffset.UtcNow,
            Intent = new() { Route = "/apps/survey", Visibility = "public" }
        }, version.VersionId);

    private static StudioPackageDraft Draft(string serviceId) => new()
    {
        DraftId = Guid.NewGuid(), ItemId = Guid.NewGuid(), PackageKey = "survey-app", Family = StudioPackageFamily.App,
        TenantId = "tenant-a", OwnerId = "owner", Generation = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Envelope = Envelope(serviceId)
    };

    private static StudioPackageEnvelope Envelope(string serviceId) => new()
    {
        Family = StudioPackageFamily.App, SchemaVersion = "studio_app.v1",
        Dependencies = [new() { Kind = "service", Ref = serviceId }]
    };
}
