// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioGuardedRestoreTests
{
    [UnitTest]
    public async Task Restore_OriginalIdentityAndDraftSurviveReplayAfterLaterEdit()
    {
        var (store, draft, first, second) = await SeedAsync();
        var before = (await store.GetPointersAsync(draft.ItemId))!;
        var conditions = Conditions(first, before);
        var receipt = await store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Current,
            "agent", "original", conditions);
        receipt.Pointers.CurrentVersionId.Should().Be(first.VersionId);
        receipt.Pointers.PublishedVersionId.Should().BeNull();
        receipt.Pointers.PointerRevision.Should().Be(before.PointerRevision + 1);
        (await store.GetDraftAsync(draft.DraftId)).Should().Be(draft);
        (await store.ListVersionsAsync(draft.ItemId)).Should().HaveCount(2);

        await store.RollbackAsync(draft.ItemId, second.VersionId, StudioRollbackPointer.Current, "human", "later edit");
        var later = (await store.GetPointersAsync(draft.ItemId))!;
        var retry = await store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Current,
            "agent", "original", conditions);
        retry.Should().Be(receipt);
        (await store.GetPointersAsync(draft.ItemId)).Should().Be(later);
        var changed = () => store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Both,
            "agent", "original", conditions);
        await changed.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetPointersAsync(draft.ItemId)).Should().Be(later);
    }

    [UnitTest]
    public async Task Restore_RejectsInterveningChangeBackToSamePointer()
    {
        var (store, draft, first, second) = await SeedAsync();
        var before = (await store.GetPointersAsync(draft.ItemId))!;
        var conditions = Conditions(first, before);
        await store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Current, "human", "change");
        await store.RollbackAsync(draft.ItemId, second.VersionId, StudioRollbackPointer.Current, "human", "return");
        var current = (await store.GetPointersAsync(draft.ItemId))!;
        current.CurrentVersionId.Should().Be(before.CurrentVersionId);
        current.PointerRevision.Should().BeGreaterThan(before.PointerRevision);
        var stale = () => store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Current, "agent", null, conditions);
        await stale.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetPointersAsync(draft.ItemId)).Should().Be(current);
        (await store.GetDraftAsync(draft.DraftId)).Should().Be(draft);
    }

    [UnitTest]
    public async Task Restore_RejectsWrongHashAndConcurrentSecondApproval()
    {
        var (store, draft, first, _) = await SeedAsync();
        var before = (await store.GetPointersAsync(draft.ItemId))!;
        var conditions = Conditions(first, before);
        var wrongHash = () => store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Current,
            "agent", null, conditions with { ContentHash = "not-the-saved-hash" });
        await wrongHash.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetPointersAsync(draft.ItemId)).Should().Be(before);

        async Task<bool> AttemptAsync(Guid requestId)
        {
            try
            {
                await store.RollbackAsync(draft.ItemId, first.VersionId, StudioRollbackPointer.Current,
                    "agent", null, conditions with { RequestId = requestId });
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => AttemptAsync(Guid.NewGuid())), Task.Run(() => AttemptAsync(Guid.NewGuid())));
        outcomes.Count(value => value).Should().Be(1);
        (await store.GetPointersAsync(draft.ItemId))!.PointerRevision.Should().Be(before.PointerRevision + 1);
        (await store.ListVersionsAsync(draft.ItemId)).Should().HaveCount(2);
    }

    private static StudioRollbackPreconditions Conditions(StudioContentVersion version, StudioContentItemPointers pointers) => new()
    {
        RequestId = Guid.NewGuid(),
        ContentHash = version.ContentHash,
        ExpectedCurrentVersionId = pointers.CurrentVersionId,
        ExpectedPointerRevision = pointers.PointerRevision,
    };

    private static async Task<(InMemoryStudioPackageStore Store, StudioPackageDraft Draft, StudioContentVersion First, StudioContentVersion Second)> SeedAsync()
    {
        var store = new InMemoryStudioPackageStore();
        var draft = new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            PackageKey = "restore-test",
            Family = StudioPackageFamily.Query,
            OwnerId = "author",
            TenantId = "tenant",
            Generation = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Query,
                SchemaVersion = "1.0",
                Format = "studio_query_package.v1",
                Body = JsonSerializer.Deserialize<JsonElement>("{\"where\":\"1=1\"}"),
            },
            Validation = StudioValidationSummary.NotValidated,
        };
        draft = await store.CreateDraftAsync(draft);
        var first = await store.CreateVersionAsync(draft, "original", "author");
        draft = (await store.UpdateDraftAsync(draft with
        {
            Envelope = draft.Envelope with { Body = JsonSerializer.Deserialize<JsonElement>("{\"where\":\"2=2\"}") },
        }))!;
        var second = await store.CreateVersionAsync(draft, "later", "author");
        return (store, draft, first, second);
    }
}
