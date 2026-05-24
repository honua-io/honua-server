// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Store-level invariants for <see cref="InMemoryConsoleContentStore"/> that the
/// endpoint layer relies on for contract correctness.
/// </summary>
public class InMemoryConsoleContentStoreTests
{
    private static ConsoleContentItem Seed(string id, ConsoleVisibility visibility, string? teamScopeId)
    {
        return new ConsoleContentItem
        {
            Id = id,
            Name = id,
            ItemType = ConsoleContentItemType.Layer,
            Visibility = visibility,
            TeamScopeId = teamScopeId,
            OwnerId = "tester",
        };
    }

    [UnitTest]
    public async Task PatchAsync_SettingVisibilityToTeam_OnItemWithoutTeamScope_Throws()
    {
        // Closes the TOCTOU window between the endpoint's pre-flight team-scope
        // check and the swap: even if the pre-check passed against a snapshot
        // that had a team scope, a concurrent PUT could have cleared it by the
        // time the patch reaches the store. The CAS loop reads the latest
        // snapshot, so this invariant must be enforced there.
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        await store.CreateAsync(Seed("toctou-1", ConsoleVisibility.Organization, teamScopeId: null));

        var patch = new ConsoleContentItemPatch { Visibility = ConsoleVisibility.Team };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PatchAsync("toctou-1", patch));
        Assert.Contains("teamScopeId", ex.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task PatchAsync_SettingVisibilityToTeam_OnItemWithTeamScope_Succeeds()
    {
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        await store.CreateAsync(Seed("happy-1", ConsoleVisibility.Organization, teamScopeId: "team-a"));

        var patch = new ConsoleContentItemPatch { Visibility = ConsoleVisibility.Team };
        var result = await store.PatchAsync("happy-1", patch);

        Assert.NotNull(result);
        Assert.Equal(ConsoleVisibility.Team, result!.Visibility);
        Assert.Equal("team-a", result.TeamScopeId);
    }

    [UnitTest]
    public async Task PatchAsync_NotChangingVisibility_OnTeamItem_PreservesTeamScope()
    {
        // Sanity check that the invariant only fires when the patch *changes*
        // visibility to team on an item without a scope — patches that don't
        // touch visibility should pass through.
        var store = new InMemoryConsoleContentStore(TimeProvider.System);
        await store.CreateAsync(Seed("noop-1", ConsoleVisibility.Team, teamScopeId: "team-b"));

        var patch = new ConsoleContentItemPatch { Title = "renamed" };
        var result = await store.PatchAsync("noop-1", patch);

        Assert.NotNull(result);
        Assert.Equal(ConsoleVisibility.Team, result!.Visibility);
        Assert.Equal("team-b", result.TeamScopeId);
        Assert.Equal("renamed", result.Title);
    }
}
