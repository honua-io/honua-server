// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Verifies that provenance references survive store round-trips and that
/// transitive resolution walks across the documented kinds (catalog-resource →
/// published-service → studio-artifact → generated-app).
/// </summary>
public class ConsoleProvenanceTests
{
    private static ConsoleContentItem Item(string id, ConsoleContentItemType type, params ConsoleProvenanceRef[] refs)
    {
        return new ConsoleContentItem
        {
            Id = id,
            Name = id,
            ItemType = type,
            Visibility = ConsoleVisibility.Organization,
            OwnerId = "tester",
            Provenance = refs,
        };
    }

    [UnitTest]
    public async Task GetProvenanceChain_Resolves_PublishedService_To_CatalogResource_To_StudioArtifact_To_GeneratedApp()
    {
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        await store.CreateAsync(Item("catalog-1", ConsoleContentItemType.Layer));
        await store.CreateAsync(Item("svc-1", ConsoleContentItemType.Service,
            new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "catalog-1", Rel = "publishes" }));
        await store.CreateAsync(Item("studio-1", ConsoleContentItemType.SavedMap,
            new ConsoleProvenanceRef { Kind = "published-service", ItemId = "svc-1", Rel = "derived-from" }));
        await store.CreateAsync(Item("app-1", ConsoleContentItemType.GeneratedApp,
            new ConsoleProvenanceRef { Kind = "studio-artifact", ItemId = "studio-1", Rel = "generated-by" }));

        var chain = await store.GetProvenanceChainAsync("app-1", maxDepth: 5, CancellationToken.None);

        Assert.Collection(chain,
            anchor => Assert.Equal("app-1", anchor.Id),
            studio => Assert.Equal("studio-1", studio.Id),
            svc => Assert.Equal("svc-1", svc.Id),
            catalog => Assert.Equal("catalog-1", catalog.Id));
    }

    [UnitTest]
    public async Task GetProvenanceChain_RespectsMaxDepth()
    {
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        await store.CreateAsync(Item("a", ConsoleContentItemType.Layer));
        await store.CreateAsync(Item("b", ConsoleContentItemType.Service,
            new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "a", Rel = "publishes" }));
        await store.CreateAsync(Item("c", ConsoleContentItemType.Service,
            new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "b", Rel = "derived-from" }));

        var chain = await store.GetProvenanceChainAsync("c", maxDepth: 1, CancellationToken.None);

        Assert.Equal(2, chain.Count);
        Assert.Equal("c", chain[0].Id);
        Assert.Equal("b", chain[1].Id);
    }

    [UnitTest]
    public async Task GetProvenanceChain_BreaksCycles()
    {
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        await store.CreateAsync(Item("a", ConsoleContentItemType.Layer,
            new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "b", Rel = "derived-from" }));
        await store.CreateAsync(Item("b", ConsoleContentItemType.Service,
            new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "a", Rel = "publishes" }));

        var chain = await store.GetProvenanceChainAsync("a", maxDepth: 5, CancellationToken.None);

        Assert.Equal(2, chain.Count);
        Assert.Equal("a", chain[0].Id);
        Assert.Equal("b", chain[1].Id);
    }

    [UnitTest]
    public async Task GetProvenanceChain_UnknownAnchor_ReturnsEmpty()
    {
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        var chain = await store.GetProvenanceChainAsync("missing", maxDepth: 5, CancellationToken.None);
        Assert.Empty(chain);
    }

    [UnitTest]
    public async Task ProvenanceRefs_SurviveCrudRoundTrips()
    {
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        var item = Item("x", ConsoleContentItemType.Report,
            new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "catalog-7", Rel = "input-of", Namespace = "ns" });

        var created = await store.CreateAsync(item);
        var fetched = await store.GetAsync(created.Id);

        Assert.NotNull(fetched);
        var refSlot = Assert.Single(fetched!.Provenance);
        Assert.Equal("catalog-7", refSlot.ItemId);
        Assert.Equal("input-of", refSlot.Rel);
        Assert.Equal("ns", refSlot.Namespace);
    }
}
