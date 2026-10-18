// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class StylePackageConsumerIndexTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true"));
    private const string Style = """{"version":8,"sources":{},"layers":[{"id":"background","type":"background"}]}""";
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    public async Task CanonicalIndex_TracksDraftsHistoricalVersionsAndApps_ExcludesInlineAndForgedDependencies()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var packages = scope.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        var styleId = "consumer-live-" + Guid.NewGuid().ToString("N");
        var forgedId = "consumer-forged-" + Guid.NewGuid().ToString("N");
        await catalog.CreateStyleAsync(styleId, Style);
        await catalog.CreateStyleAsync(forgedId, Style);
        var map = Draft(StudioPackageFamily.Map, key => new { mapPackageId = key, styleRefs = new[] { new { styleId } } });
        map = map with { Envelope = map.Envelope with { Dependencies = [new() { Kind = "server-live-style", Ref = forgedId }] } };
        map = await packages.CreateDraftAsync(map);
        var saved = await packages.CreateVersionAsync(map, "live appearance", "consumer-owner");
        var app = await packages.CreateDraftAsync(Draft(StudioPackageFamily.App, _ => new { mapPackageId = map.PackageKey }));
        var savedApp = await packages.CreateVersionAsync(app, "reference existing map", "consumer-owner");
        // Seed an accepted canonical publication to verify index semantics; the
        // separate HTTP agent journey qualifies proposal/approval execution.
        await packages.CreatePublicationRequestAsync(new()
        {
            RequestId = Guid.NewGuid(),
            ItemId = savedApp.ItemId,
            VersionId = savedApp.VersionId,
            Status = StudioPublicationRequestStatus.Accepted,
            Validation = savedApp.Validation,
            RequestedBy = "consumer-owner",
            CreatedAt = DateTimeOffset.UtcNow,
        }, savedApp.VersionId);
        var inline = await packages.CreateDraftAsync(Draft(StudioPackageFamily.Map,
            key => new { mapPackageId = key, styleRefs = new[] { new { styleId, body = new { } } } }));
        await packages.CreateVersionAsync(inline, "detached appearance", "consumer-owner");
        var snapshot = await catalog.GetEditSnapshotAsync(styleId, null);
        snapshot.ConsumerIndexComplete.Should().BeTrue();
        snapshot.Consumers.Should().Contain(consumer => consumer.DraftId == map.DraftId);
        snapshot.Consumers.Should().Contain(consumer => consumer.VersionId == saved.VersionId);
        snapshot.Consumers.Should().Contain(consumer => consumer.DraftId == app.DraftId);
        snapshot.Consumers.Should().Contain(consumer => consumer.VersionId == savedApp.VersionId && consumer.IsPublished);
        snapshot.Consumers.Should().NotContain(consumer => consumer.ItemId == inline.ItemId);
        (await catalog.GetEditSnapshotAsync(forgedId, null)).Consumers.Should().BeEmpty();
        // Removing a mutable reference cannot erase the still-loadable immutable dependency.
        await packages.UpdateDraftAsync(map with
        {
            Envelope = map.Envelope with
            { Body = JsonSerializer.SerializeToElement(new { mapPackageId = map.PackageKey, styleRefs = Array.Empty<object>() }) }
        });
        var after = await catalog.GetEditSnapshotAsync(styleId, null);
        after.Consumers.Should().NotContain(consumer => consumer.DraftId == map.DraftId);
        after.Consumers.Should().Contain(consumer => consumer.VersionId == saved.VersionId);
        after.Consumers.Should().Contain(consumer => consumer.VersionId == savedApp.VersionId && consumer.IsPublished);
        (await catalog.ApplyEditIfSnapshotAsync(snapshot, Style, "{}", "stale-consumer-review", _ => Task.CompletedTask)).Should().BeNull();
        var secondAppVersion = await packages.CreateVersionAsync(app, "second saved version", "consumer-owner");
        await packages.CreatePublicationRequestAsync(new()
        {
            RequestId = Guid.NewGuid(),
            ItemId = secondAppVersion.ItemId,
            VersionId = secondAppVersion.VersionId,
            Status = StudioPublicationRequestStatus.Accepted,
            Validation = secondAppVersion.Validation,
            RequestedBy = "consumer-owner",
            CreatedAt = DateTimeOffset.UtcNow,
        }, secondAppVersion.VersionId);
        var replaced = await catalog.GetEditSnapshotAsync(styleId, null);
        replaced.Consumers.Should().Contain(consumer => consumer.VersionId == secondAppVersion.VersionId && consumer.IsPublished);
        replaced.Consumers.Should().Contain(consumer => consumer.VersionId == savedApp.VersionId && !consumer.IsPublished);
        await packages.RollbackAsync(app.ItemId, savedApp.VersionId, StudioRollbackPointer.Published, "consumer-owner", "restore pointer");
        var rolledBack = await catalog.GetEditSnapshotAsync(styleId, null);
        rolledBack.Consumers.Should().Contain(consumer => consumer.VersionId == savedApp.VersionId && consumer.IsPublished);
        (await catalog.ApplyEditIfSnapshotAsync(replaced, Style, "{}", "stale-published-impact", _ => Task.CompletedTask)).Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    public async Task CanonicalIndex_ResolvesAppCreatedBeforeMap_WithoutCrossWorkspaceNameGuessing()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var packages = scope.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        var styleId = "consumer-future-" + Guid.NewGuid().ToString("N");
        await catalog.CreateStyleAsync(styleId, Style);
        var key = "future-map-" + Guid.NewGuid().ToString("N");
        var app = await packages.CreateDraftAsync(Draft(StudioPackageFamily.App, _ => new { mapPackageId = key }));
        (await catalog.GetEditSnapshotAsync(styleId, null)).ConsumerIndexComplete.Should().BeFalse();
        var map = await packages.CreateDraftAsync(Draft(StudioPackageFamily.Map,
            _ => new { mapPackageId = key, styleRefs = new[] { new { styleId } } }, packageKey: key));
        await packages.CreateVersionAsync(map, "resolve actual target", "consumer-owner");
        var otherMap = await packages.CreateDraftAsync(Draft(StudioPackageFamily.Map,
            _ => new { mapPackageId = key, styleRefs = Array.Empty<object>() }, workspace: "other-workspace", packageKey: key));
        await packages.CreateVersionAsync(otherMap, "unrelated same name", "consumer-owner");
        var otherApp = await packages.CreateDraftAsync(Draft(StudioPackageFamily.App,
            _ => new { mapPackageId = key }, workspace: "other-workspace"));
        var resolved = await catalog.GetEditSnapshotAsync(styleId, null);
        resolved.ConsumerIndexComplete.Should().BeTrue();
        resolved.Consumers.Should().Contain(consumer => consumer.DraftId == app.DraftId);
        resolved.Consumers.Should().NotContain(consumer => consumer.ItemId == otherApp.ItemId);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task CanonicalBarrier_TwoStoreScopes_BlockConcurrentReferenceCreationUntilStyleCommit()
    {
        await using var first = _fixture.Services.CreateAsyncScope();
        await using var second = _fixture.Services.CreateAsyncScope();
        var catalog = first.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var packages = second.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        var styleId = "consumer-race-" + Guid.NewGuid().ToString("N");
        await catalog.CreateStyleAsync(styleId, Style);
        var before = await catalog.GetEditSnapshotAsync(styleId, null);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = catalog.ApplyEditIfSnapshotAsync(before, Style, "{}", "consumer-owner", async token =>
        { locked.SetResult(); await release.Task.WaitAsync(token); });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var draft = Draft(StudioPackageFamily.Map, key => new { mapPackageId = key, styleRefs = new[] { new { styleId } } });
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var create = () => packages.CreateDraftAsync(draft, cancellation.Token);
            await create.Should().ThrowAsync<OperationCanceledException>();
            (await packages.GetDraftAsync(draft.DraftId)).Should().BeNull();
        }
        finally { release.TrySetResult(); }
        (await mutation).Should().NotBeNull();
        await packages.CreateDraftAsync(draft);
        var after = await catalog.GetEditSnapshotAsync(styleId, null);
        after.Consumers.Should().ContainSingle(consumer => consumer.DraftId == draft.DraftId);
        after.CatalogStyle!.StyleVersion.Should().Be(before.CatalogStyle!.StyleVersion + 1);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    public async Task ImpactHttp_HidesUnauthorizedPackageIdentityAndCount_AndDisablesSharedEdit()
    {
        using var admin = _fixture.CreateAdminClient();
        using var issued = await admin.PostAsJsonAsync("/api/v1/admin/api-keys", new
        { name = "consumer-reader", permissions = new[] { "admin:read" } });
        issued.StatusCode.Should().Be(HttpStatusCode.Created);
        using var keyJson = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var key = keyJson.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        using var reader = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
        await using var scope = _fixture.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var packages = scope.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        var styleId = "consumer-hidden-" + Guid.NewGuid().ToString("N");
        await catalog.CreateStyleAsync(styleId, Style);
        var hidden = await packages.CreateDraftAsync(Draft(StudioPackageFamily.Map,
            name => new { mapPackageId = name, styleRefs = new[] { new { styleId } } }, owner: "another-owner"));
        await packages.CreateVersionAsync(hidden, "private consumer", "another-owner");
        using var response = await reader.GetAsync("/ogc/styles/" + styleId + "/impact");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(hidden.PackageKey).And.NotContain(hidden.ItemId.ToString("D"));
        using var impact = JsonDocument.Parse(text);
        impact.RootElement.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        impact.RootElement.GetProperty("impactComplete").GetBoolean().Should().BeFalse();
        impact.RootElement.GetProperty("consumers").GetArrayLength().Should().Be(0);
        impact.RootElement.TryGetProperty("totalConsumers", out _).Should().BeFalse();
        impact.RootElement.TryGetProperty("hiddenCount", out _).Should().BeFalse();
    }

    private static StudioPackageDraft Draft(StudioPackageFamily family, Func<string, object> body,
        string workspace = "consumer-workspace", string? packageKey = null, string owner = "consumer-owner")
    {
        var key = packageKey ?? "consumer-package-" + Guid.NewGuid().ToString("N");
        return new()
        {
            DraftId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            PackageKey = key,
            WorkspaceId = workspace,
            OwnerId = owner,
            TenantId = "public",
            Family = family,
            Generation = 1,
            Envelope = new() { Family = family, SchemaVersion = "1.0", Body = JsonSerializer.SerializeToElement(body(key)) },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}
