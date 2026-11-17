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
    [InlineData(StudioPackageFamily.Map, "mapPackageId", "mapId", "honua_map_package.v1", "honua_map.v1")]
    [InlineData(StudioPackageFamily.App, "appPackageId", "appId", "honua_app_package.v1", "honua_app.v1")]
    public async Task ReopenVersion_NormalizesLegacyIdentityWithoutChangingVersion(
        StudioPackageFamily family, string oldId, string newId, string oldFormat, string newFormat)
    {
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
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("{\"mapPackageId\":\"old\",\"mapId\":\"new\"}")]
    [InlineData("{\"appPackageId\":\"old\",\"appId\":\"new\"}")]
    [InlineData("[]")]
    public async Task ReopenVersion_RejectsAmbiguousOrMalformedHistoricalBody(string json)
    {
        using var provider = BuildServiceProvider();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var version = await StoreHistoricalVersionAsync(store, new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.App, SchemaVersion = "1.0",
            Body = JsonDocument.Parse(json).RootElement.Clone(),
        });
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        await Assert.ThrowsAsync<JsonException>(() =>
            service.ReopenVersionAsync(version.ItemId, version.VersionId, "tester"));
        var unchanged = await store.GetVersionAsync(version.ItemId, version.VersionId);
        Assert.Equal(version.ContentHash, unchanged!.ContentHash);
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
            Body = JsonDocument.Parse("{\"appId\":\"app-one\",\"mapPackageId\":\"map-one\",\"mapId\":\"map-one\"}")
                .RootElement.Clone(),
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

    private static Task<StudioContentVersion> StoreHistoricalVersionAsync(
        IStudioPackageStore store, StudioPackageEnvelope envelope) => store.CreateVersionAsync(
        new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(), ItemId = Guid.NewGuid(), PackageKey = "legacy-map-app",
            Family = envelope.Family, Envelope = envelope, CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, "saved before vocabulary change", "tester");

}
