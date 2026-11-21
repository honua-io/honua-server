// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Studio.Services.Bridging;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Studio;

public sealed partial class StudioPackageLifecycleServiceTests
{
    [UnitTheory]
    [InlineData("create-input")]
    [InlineData("create-result")]
    [InlineData("read-result")]
    [InlineData("list-result")]
    [InlineData("update-input")]
    [InlineData("update-result")]
    public async Task UpdateDraft_MutableActiveEnvelope_CannotCorruptPriorRevision(string source)
    {
        foreach (IStudioPackageStore store in new IStudioPackageStore[]
        {
            new InMemoryStudioPackageStore(),
            new BridgedStudioPackageStore(new InMemoryStudioPackageStore(), []),
        })
        {
            var bindings = BuildEnvelope("original", "content.parcels").Bindings.ToList();
            var service = BuildServiceProvider(store).GetRequiredService<IStudioPackageLifecycleService>();
            var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
            {
                PackageKey = "isolated-active-revision",
                Envelope = BuildEnvelope("original", "content.parcels") with { Bindings = bindings },
            });
            StudioPackageEnvelope exposed = source switch
            {
                "create-input" => draft.Envelope with { Bindings = bindings },
                "create-result" => draft.Envelope,
                "read-result" => (await store.GetDraftAsync(draft.DraftId))!.Envelope,
                "list-result" => (await store.ListDraftsAsync(new())).Items.Single().Envelope,
                _ => draft.Envelope with { Bindings = bindings },
            };
            if (source.StartsWith("update-", StringComparison.Ordinal))
            {
                draft = (await store.UpdateDraftAsync(draft with { Envelope = exposed }))!;
                if (source == "update-result")
                {
                    exposed = draft.Envelope;
                }
            }

            Assert.IsAssignableFrom<IList<StudioPackageBinding>>(exposed.Bindings).Clear();
            var current = (await store.GetDraftAsync(draft.DraftId))!;
            Assert.Single(current.Envelope.Bindings);
            await store.UpdateDraftAsync(current with { Envelope = BuildEnvelope("changed", "content.roads") });
            var revision = (await store.GetDraftRevisionAsync(draft.DraftId, draft.Generation))!;
            Assert.Single(revision.Envelope.Bindings);
        }
    }

    [UnitTest]
    public async Task GetDraftRevision_MutableEnvelopeCollections_CannotChangeStoredHistory()
    {
        foreach (IStudioPackageStore store in new IStudioPackageStore[]
        {
            new InMemoryStudioPackageStore(),
            new BridgedStudioPackageStore(new InMemoryStudioPackageStore(), []),
        })
        {
            var bindings = BuildEnvelope("original", "content.parcels").Bindings.ToList();
            var service = BuildServiceProvider(store).GetRequiredService<IStudioPackageLifecycleService>();
            var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
            {
                PackageKey = "isolated-revision",
                Envelope = BuildEnvelope("original", "content.parcels") with { Bindings = bindings },
            });
            await store.UpdateDraftAsync(draft with { Envelope = BuildEnvelope("changed", "content.roads") });
            bindings.Clear();
            var revision = await store.GetDraftRevisionAsync(draft.DraftId, draft.Generation);
            Assert.NotNull(revision);
            Assert.Single(revision.Envelope.Bindings);
            Assert.IsAssignableFrom<IList<StudioPackageBinding>>(revision.Envelope.Bindings).Clear();
            var reread = await store.GetDraftRevisionAsync(draft.DraftId, draft.Generation);
            Assert.NotNull(reread);
            Assert.Single(reread.Envelope.Bindings);
        }
    }

    [UnitTest]
    public async Task UpdateDraft_RevisionsPreservePriorEnvelopeAndRejectStaleWrites()
    {
        foreach (IStudioPackageStore store in new IStudioPackageStore[]
        {
            new InMemoryStudioPackageStore(),
            new BridgedStudioPackageStore(new InMemoryStudioPackageStore(), []),
        })
        {
            var service = BuildServiceProvider(store).GetRequiredService<IStudioPackageLifecycleService>();
            var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
            {
                PackageKey = "revision-test",
                Envelope = BuildEnvelope("original", "content.parcels"),
                ActorId = "creator",
            });
            var updated = await store.UpdateDraftAsync(draft with { Envelope = BuildEnvelope("changed", "content.roads") });
            Assert.NotNull(updated);
            var revision = await store.GetDraftRevisionAsync(draft.DraftId, draft.Generation);
            Assert.NotNull(revision);
            Assert.Equal("original", revision.Envelope.Body!.Value.GetProperty("where").GetString());
            Assert.Equal(
                JsonSerializer.Serialize(draft.Envelope, StudioJsonContext.Default.StudioPackageEnvelope),
                JsonSerializer.Serialize(revision.Envelope, StudioJsonContext.Default.StudioPackageEnvelope));
            Assert.Equal(draft.UpdatedBy, revision.UpdatedBy);
            Assert.Equal(draft.UpdatedAt, revision.UpdatedAt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateDraftAsync(draft));
            Assert.Single(await store.ListDraftRevisionsAsync(new(draft.DraftId)));
            Assert.Null(await store.GetDraftRevisionAsync(Guid.NewGuid(), draft.Generation));
            await store.DeleteDraftAsync(draft.DraftId, updated.Generation);
            Assert.Empty(await store.ListDraftRevisionsAsync(new(draft.DraftId)));
            Assert.Null(await store.GetDraftRevisionAsync(draft.DraftId, draft.Generation));
        }
    }

    [UnitTest]
    public async Task PreviewPlanAsync_ReturnsFreshValidationWithoutMutatingDraftOrRecordingRevision()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryStudioPackageStore();
        var service = BuildServiceProvider(store, timeProvider: clock).GetRequiredService<IStudioPackageLifecycleService>();
        var created = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "readonly-preview",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "author-a",
        });

        clock.Advance(TimeSpan.FromMinutes(10));
        var plan = await service.PreviewPlanAsync(created.DraftId, "reviewer-b");

        Assert.NotNull(plan);
        Assert.True(plan!.Synchronous);
        Assert.False(plan.RequiresJob);
        Assert.Equal(StudioPackageValidationStatus.Valid, plan.Validation.Status);
        Assert.Equal(clock.GetUtcNow(), plan.Validation.GeneratedAt);

        var reread = await store.GetDraftAsync(created.DraftId);
        Assert.NotNull(reread);
        Assert.Equal(created.Generation, reread!.Generation);
        Assert.Equal(created.UpdatedBy, reread.UpdatedBy);
        Assert.Equal(created.UpdatedAt, reread.UpdatedAt);
        Assert.Equal(created.Validation.GeneratedAt, reread.Validation.GeneratedAt);
        Assert.Empty(await store.ListDraftRevisionsAsync(new(created.DraftId)));
    }

    [UnitTest]
    public async Task RestoreDraftRevisionAsync_RestoresEnvelopeAsNewGenerationWhilePreservingIdentityAndLifecycleState()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryStudioPackageStore();
        var service = BuildServiceProvider(store, timeProvider: clock).GetRequiredService<IStudioPackageLifecycleService>();
        var created = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "undo-composition",
            WorkspaceId = "ws-build",
            OwnerId = "owner-a",
            BaseVersionId = Guid.NewGuid(),
            Envelope = BuildEnvelope("roads = 1", "content.roads"),
            ActorId = "owner-a",
        });

        clock.Advance(TimeSpan.FromMinutes(2));
        var edited = await ReplaceEnvelopeAsync(service, created, BuildEnvelope("1=0", "content.empty"));

        clock.Advance(TimeSpan.FromMinutes(3));
        var restored = await service.RestoreDraftRevisionAsync(
            created.DraftId,
            RestoreCommand(created.Generation, edited.Generation, "restorer-b"));

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Generation);
        AssertDraftIdentityPreserved(created, restored);
        Assert.NotNull(restored.BaseVersionId);
        Assert.Equal("restorer-b", restored.UpdatedBy);
        Assert.Equal(clock.GetUtcNow(), restored.UpdatedAt);
        Assert.Equal("roads = 1", restored.Envelope.Body!.Value.GetProperty("where").GetString());
        Assert.Equal(2, (await service.ListDraftRevisionsAsync(new(created.DraftId))).Count);
    }

    [UnitTest]
    public async Task RestoreDraftRevisionAsync_RejectsStaleGenerationAndForeignOrMissingRevisionWithoutWrites()
    {
        var store = new InMemoryStudioPackageStore();
        var service = BuildServiceProvider(store).GetRequiredService<IStudioPackageLifecycleService>();
        var first = await CreateDraftAsync(service, "draft-one", BuildEnvelope("one", "content.parcels"));
        var second = await CreateDraftAsync(service, "draft-two", BuildEnvelope("two", "content.roads"));
        var firstUpdated = await ReplaceEnvelopeAsync(service, first, BuildEnvelope("one-edited", "content.parcels"));
        var secondUpdated = await ReplaceEnvelopeAsync(service, second, BuildEnvelope("two-edited", "content.roads"));
        var secondThird = await ReplaceEnvelopeAsync(service, secondUpdated, BuildEnvelope("two-edited-again", "content.roads"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreDraftRevisionAsync(
            first.DraftId,
            RestoreCommand(first.Generation, first.Generation, "owner-a")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RestoreDraftRevisionAsync(
            first.DraftId,
            RestoreCommand(secondThird.Generation - 1, firstUpdated.Generation, "owner-a")));

        var reread = (await store.GetDraftAsync(first.DraftId))!;
        Assert.Equal(firstUpdated.Generation, reread.Generation);
        Assert.Equal("one-edited", reread.Envelope.Body!.Value.GetProperty("where").GetString());
        Assert.Single(await service.ListDraftRevisionsAsync(new(first.DraftId)));
    }

    private static Task<StudioPackageDraft> CreateDraftAsync(
        IStudioPackageLifecycleService service,
        string packageKey,
        StudioPackageEnvelope envelope) => service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = packageKey,
            Envelope = envelope,
            ActorId = "owner-a",
        });

    private static async Task<StudioPackageDraft> ReplaceEnvelopeAsync(
        IStudioPackageLifecycleService service,
        StudioPackageDraft current,
        StudioPackageEnvelope envelope)
    {
        var updated = await service.UpdateDraftAsync(current.DraftId, new UpdateStudioPackageDraftCommand
        {
            PackageKey = current.PackageKey,
            WorkspaceId = current.WorkspaceId,
            OwnerId = current.OwnerId,
            Envelope = envelope,
            Generation = current.Generation,
            ActorId = "owner-a",
        });
        Assert.NotNull(updated);
        return updated!;
    }

    private static RestoreStudioDraftRevisionCommand RestoreCommand(long target, long expected, string actorId) => new()
    {
        TargetGeneration = target,
        ExpectedGeneration = expected,
        ActorId = actorId,
    };

    private static void AssertDraftIdentityPreserved(StudioPackageDraft created, StudioPackageDraft restored)
    {
        Assert.Equal(created.ItemId, restored.ItemId);
        Assert.Equal(created.PackageKey, restored.PackageKey);
        Assert.Equal(created.WorkspaceId, restored.WorkspaceId);
        Assert.Equal(created.OwnerId, restored.OwnerId);
        Assert.Equal(created.TenantId, restored.TenantId);
        Assert.Equal(created.BaseVersionId, restored.BaseVersionId);
        Assert.Equal(created.CreatedBy, restored.CreatedBy);
        Assert.Equal(created.CreatedAt, restored.CreatedAt);
    }
}
