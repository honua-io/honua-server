// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Studio;

public partial class StudioPackageLifecycleServiceTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(StudioPackageFamily.Map, "honua_map_package.v1", "honua_map.v1")]
    [InlineData(StudioPackageFamily.App, "honua_app_package.v1", "honua_app.v1")]
    public async Task ReopenAndRestore_NormalizeHistoricalIdentityWithoutChangingHistory(
        StudioPackageFamily family, string oldFormat, string newFormat)
    {
        var oldId = family == StudioPackageFamily.App ? "appPackageId" : "mapPackageId";
        var newId = family == StudioPackageFamily.App ? "appId" : "mapId";
        using var provider = BuildServiceProvider();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var envelope = new StudioPackageEnvelope
        {
            Family = family, SchemaVersion = "1.0", Format = oldFormat,
            Body = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                [oldId] = "saved-id", ["format"] = oldFormat,
                ["mapSpec"] = new Dictionary<string, string> { [oldId] = "opaque" },
            }),
        };
        var version = await StoreHistoricalVersionAsync(store, envelope);
        var originalDraft = (await store.ListDraftsAsync(new())).Items.Single();
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var reopened = await service.ReopenVersionAsync(version.ItemId, version.VersionId, "tester");
        Assert.NotNull(reopened);
        Assert.Equal(newFormat, reopened.Envelope.Format);
        Assert.Equal("saved-id", reopened.Envelope.Body!.Value.GetProperty(newId).GetString());
        Assert.False(reopened.Envelope.Body.Value.TryGetProperty(oldId, out _));
        Assert.Equal(newFormat, reopened.Envelope.Body.Value.GetProperty("format").GetString());
        Assert.Equal("opaque", reopened.Envelope.Body.Value.GetProperty("mapSpec").GetProperty(oldId).GetString());
        var unchanged = await store.GetVersionAsync(version.ItemId, version.VersionId);
        Assert.Equal(version.ContentHash, unchanged!.ContentHash);
        Assert.Equal(envelope.Body.Value.GetRawText(), unchanged.Envelope.Body!.Value.GetRawText());

        // Simulate the migration archiving the pre-cutover draft before normalizing it.
        var current = await store.UpdateDraftAsync(originalDraft with { Envelope = reopened.Envelope });
        Assert.NotNull(current);
        var restored = await service.RestoreDraftRevisionAsync(originalDraft.DraftId,
            RestoreCommand(originalDraft.Generation, current.Generation, "tester"));
        Assert.NotNull(restored);
        Assert.Equal(newFormat, restored.Envelope.Format);
        Assert.Equal(reopened.Envelope.Body.Value.GetRawText(), restored.Envelope.Body!.Value.GetRawText());
        await AssertHistoricalDraftUnchangedAsync(store, originalDraft);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("{\"mapPackageId\":\"old\",\"mapId\":\"new\"}")]
    [InlineData("{\"appPackageId\":\"old\",\"appId\":\"new\"}")]
    public async Task ReopenAndRestore_RejectAmbiguousHistoricalIdentity(string json)
    {
        using var provider = BuildServiceProvider();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var version = await StoreHistoricalVersionAsync(store, new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.App, SchemaVersion = "1.0",
            Body = JsonElement.Parse(json),
        });
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        await Assert.ThrowsAsync<JsonException>(() =>
            service.ReopenVersionAsync(version.ItemId, version.VersionId, "tester"));
        var unchanged = await store.GetVersionAsync(version.ItemId, version.VersionId);
        Assert.Equal(version.ContentHash, unchanged!.ContentHash);

        var originalDraft = (await store.ListDraftsAsync(new())).Items.Single();
        var current = await store.UpdateDraftAsync(originalDraft with
        {
            Envelope = originalDraft.Envelope with { Body = JsonElement.Parse("{}") },
        });
        Assert.NotNull(current);
        await Assert.ThrowsAsync<JsonException>(() => service.RestoreDraftRevisionAsync(originalDraft.DraftId,
            RestoreCommand(originalDraft.Generation, current.Generation, "tester")));
        Assert.Equal(current.Generation, (await store.GetDraftAsync(originalDraft.DraftId))!.Generation);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("[]")]
    [InlineData("\"draft\"")]
    [InlineData(null)]
    public async Task ReopenVersion_IncompleteBody_KeepsBodyAndCanonicalizesFormat(string? json)
    {
        using var provider = BuildServiceProvider();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var version = await StoreHistoricalVersionAsync(store, new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.App, SchemaVersion = "1.0", Format = "honua_app_package.v1",
            Body = json is null ? null : JsonElement.Parse(json),
        });
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var reopened = await service.ReopenVersionAsync(version.ItemId, version.VersionId, "tester");
        Assert.NotNull(reopened);
        Assert.Equal("honua_app.v1", reopened.Envelope.Format);
        Assert.Equal(json, reopened.Envelope.Body?.GetRawText());
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ReopenVersion_NormalizesAppMapBindingAndKeepsCanonicalIdentity()
    {
        using var provider = BuildServiceProvider();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var version = await StoreHistoricalVersionAsync(store, new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.App, SchemaVersion = "1.0", Format = "honua_app.v1",
            Body = JsonElement.Parse("{\"appId\":\"app-one\",\"mapPackageId\":\"map-one\",\"mapId\":\"map-one\"}"),
        });
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var reopened = await service.ReopenVersionAsync(version.ItemId, version.VersionId, "tester");
        Assert.NotNull(reopened);
        var body = reopened.Envelope.Body!.Value;
        Assert.Equal("app-one", body.GetProperty("appId").GetString());
        Assert.Equal("map-one", body.GetProperty("mapId").GetString());
        Assert.False(body.TryGetProperty("mapPackageId", out _));
        Assert.Equal("honua_app.v1", reopened.Envelope.Format);
    }

    private static async Task AssertHistoricalDraftUnchangedAsync(IStudioPackageStore store, StudioPackageDraft originalDraft)
    {
        var revision = await store.GetDraftRevisionAsync(originalDraft.DraftId, originalDraft.Generation);
        Assert.NotNull(revision);
        Assert.Equal(originalDraft.Envelope.Format, revision.Envelope.Format);
        Assert.Equal(originalDraft.Envelope.Body!.Value.GetRawText(), revision.Envelope.Body!.Value.GetRawText());
    }

    private static async Task<StudioContentVersion> StoreHistoricalVersionAsync(
        IStudioPackageStore store, StudioPackageEnvelope envelope)
    {
        var draft = await store.CreateDraftAsync(new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(), ItemId = Guid.NewGuid(), PackageKey = "legacy-map-app", Generation = 1,
            Family = envelope.Family, Envelope = envelope, CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        return await store.CreateVersionAsync(draft, "saved before vocabulary change", "tester");
    }

}
