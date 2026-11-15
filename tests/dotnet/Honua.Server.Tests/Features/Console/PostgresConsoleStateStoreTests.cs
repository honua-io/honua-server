// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Console;

// Each store instance is one task; instances over the same tables are a two-task deployment, and a new
// instance after the others are gone is a restart.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class PostgresConsoleStateStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ContentCreatedOnTaskA_IsServedByTaskBAndAfterRestart()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Content(harness);
        var taskB = Content(harness);
        (await taskB.ListAsync(new ConsoleContentQuery())).Items.Should().BeEmpty();

        var created = await taskA.CreateAsync(Item("roads", ConsoleVisibility.Organization));

        (await taskB.GetAsync(created.Id))!.Name.Should().Be("roads");
        (await taskB.ListAsync(new ConsoleContentQuery())).Items.Should().ContainSingle();
        (await Content(harness).GetAsync(created.Id))!.Generation.Should().Be(1);
    }

    [IntegrationTest]
    public async Task ContentVisibilityPatchedAndDeletedOnTaskA_IsSeenOnTaskB()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Content(harness);
        var taskB = Content(harness);
        var created = await taskA.CreateAsync(Item("parcels", ConsoleVisibility.Personal));
        (await taskB.GetAsync(created.Id))!.Visibility.Should().Be(ConsoleVisibility.Personal);

        await taskA.PatchAsync(created.Id, new ConsoleContentItemPatch { Visibility = ConsoleVisibility.Public });
        (await taskB.GetAsync(created.Id))!.Visibility.Should().Be(ConsoleVisibility.Public);

        (await taskA.DeleteAsync(created.Id)).Should().BeTrue();
        (await taskB.GetAsync(created.Id)).Should().BeNull();
        (await taskB.DeleteAsync(created.Id)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task ContentUpdatedOnTaskA_RefusesStaleGenerationOnTaskB()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Content(harness);
        var taskB = Content(harness);
        var created = await taskA.CreateAsync(Item("rivers", ConsoleVisibility.Organization));
        var seenByB = await taskB.GetAsync(created.Id);

        await taskA.UpdateAsync(created with { Title = "Rivers (A)" });

        var stale = () => taskB.UpdateAsync(seenByB! with { Title = "Rivers (B)" });
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("Stale generation*");
        (await taskB.GetAsync(created.Id))!.Title.Should().Be("Rivers (A)");
        var duplicate = () => taskB.CreateAsync(Item("rivers", ConsoleVisibility.Organization) with { Id = created.Id });
        await duplicate.Should().ThrowAsync<InvalidOperationException>();
    }

    [IntegrationTest]
    public async Task PublicLinkMintedOnTaskA_ResolvesOnTaskB_AndExpiryOnTaskB_RefusesItOnTaskA()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Shares(harness);
        var taskB = Shares(harness);
        var link = await taskA.MintPublicLinkAsync("item-1", ConsoleShareAccessTier.PublicLink, null, "owner");

        var resolved = await taskB.ResolvePublicLinkAsync(link.Token!);
        resolved!.ItemId.Should().Be("item-1");
        resolved.AccessTier.Should().Be(ConsoleShareAccessTier.PublicLink);

        (await taskB.ExpirePublicLinkAsync("item-1", link.TokenId, "owner")).Should().BeTrue();
        (await taskA.ResolvePublicLinkAsync(link.Token!)).Should().BeNull();
        (await Shares(harness).ListPublicLinksAsync("item-1")).Should().ContainSingle().Which.IsExpired.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task PublicLinkStored_KeepsOnlyTheDigestOfTheToken()
    {
        await using var harness = await CreateHarnessAsync();
        var link = await Shares(harness).MintPublicLinkAsync("item-2", ConsoleShareAccessTier.PublicLink, null, "owner");

        var stored = await harness.CreateConfigurationDocuments().ReadAsync(
            new ConfigurationCollection<StoredConsoleShare>("console-share", ConsoleStateJsonContext.Default.StoredConsoleShare),
            CancellationToken.None);

        var document = stored.Documents.Should().ContainSingle().Subject;
        JsonSerializer.Serialize(document.Value, ConsoleStateJsonContext.Default.StoredConsoleShare).Should().NotContain(link.Token!);
        (await Shares(harness).ListPublicLinksAsync("item-2")).Should().ContainSingle().Which.Token.Should().BeNull();
        (await Shares(harness).ResolvePublicLinkAsync(link.Token!))!.TokenId.Should().Be(link.TokenId);
    }

    [IntegrationTest]
    public async Task EmbedSwitchedOffOnTaskB_RefusesTokenMintedOnTaskA()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Shares(harness);
        var taskB = Shares(harness);
        await taskA.SetEmbedAsync("map-1", ConsoleShareAccessTier.PublicLink, true, ConsoleEmbedAudience.Map, "owner");
        var token = await taskA.MintEmbedTokenAsync(
            "map-1", ConsoleShareAccessTier.PublicLink, ConsoleEmbedAudience.Map, TimeSpan.FromMinutes(10), "owner");

        (await taskB.RedeemEmbedTokenAsync(token.Token!))!.ItemId.Should().Be("map-1");
        (await taskB.GetAsync("map-1"))!.EmbedAudience.Should().Be(ConsoleEmbedAudience.Map);

        await taskB.SetEmbedAsync("map-1", ConsoleShareAccessTier.PublicLink, false, null, "owner");
        (await taskA.RedeemEmbedTokenAsync(token.Token!)).Should().BeNull();
        (await taskA.RedeemEmbedTokenAsync("not-a-token")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task OpenDataPageSavedOnTaskA_IsServedByTaskBAndAfterRestart()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = OpenData(harness);
        var taskB = OpenData(harness);
        (await taskB.GetPageAsync("item-3")).Should().BeNull();

        await taskA.SavePageAsync(new ConsoleOpenDataPage { ItemId = "item-3", Title = "Bus stops", License = "CC-BY-4.0" }, "editor");

        var page = await taskB.GetPageAsync("item-3");
        page!.Title.Should().Be("Bus stops");
        page.UpdatedById.Should().Be("editor");
        (await OpenData(harness).GetPageAsync("item-3"))!.License.Should().Be("CC-BY-4.0");
    }

    [IntegrationTest]
    public async Task StacPublishedOnTaskA_IsListedOnTaskB_AndWithdrawalOnTaskB_KeepsTheCollectionId()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = OpenData(harness);
        var taskB = OpenData(harness);
        var first = await taskA.PublishStacAsync("item-4", "editor");
        (await taskB.ListPublishedStacItemIdsAsync()).Should().Equal("item-4");

        (await taskB.UnpublishStacAsync("item-4", "editor"))!.Status.Should().Be(ConsoleStacPublicationStatus.Unpublished);
        (await taskA.ListPublishedStacItemIdsAsync()).Should().BeEmpty();
        (await taskA.UnpublishStacAsync("item-4", "editor")).Should().BeNull();

        var again = await taskA.PublishStacAsync("item-4", "editor");
        again.CollectionId.Should().Be(first.CollectionId);
        again.FirstPublishedAt.Should().Be(first.FirstPublishedAt);
        (await taskB.GetStacPublicationAsync("item-4")).Revision.Should().Be(3);
    }

    private static ConsoleContentItem Item(string name, ConsoleVisibility visibility) => new()
    {
        Id = string.Empty,
        Name = name,
        ItemType = ConsoleContentItemType.Layer,
        Visibility = visibility,
        OwnerId = "owner",
    };

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConsoleStateStoreTests));

    private static PostgresConsoleContentStore Content(ControlPlaneStateHarness harness)
        => new(harness.CreateConfigurationDocuments(), TimeProvider.System);

    private static PostgresConsoleShareStore Shares(ControlPlaneStateHarness harness)
        => new(harness.CreateConfigurationDocuments(), harness.CreateSecurityTokenStore(), TimeProvider.System);

    private static PostgresConsoleOpenDataStore OpenData(ControlPlaneStateHarness harness)
        => new(harness.CreateConfigurationDocuments(), TimeProvider.System);
}
