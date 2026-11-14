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
}
