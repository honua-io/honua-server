// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Admin.Services;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Concurrency and immutable-name coverage for the fallback role store.</summary>
public sealed class InMemoryRoleStoreTests
{
    [Fact]
    public async Task CreateRoleAsync_ConcurrentCaseInsensitiveDuplicates_CreatesExactlyOneRole()
    {
        const int attemptCount = 32;
        var store = new InMemoryRoleStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, attemptCount)
            .Select(async attempt =>
            {
                await release.Task;
                try
                {
                    return await store.CreateRoleAsync(new RoleDefinition
                    {
                        Name = attempt % 2 == 0 ? "Concurrent-Reviewer" : "concurrent-reviewer",
                    });
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            })
            .ToArray();

        release.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(static role => role is not null).Should().Be(1);
        var matchingRoles = (await store.ListRolesAsync())
            .Where(static role => string.Equals(
                role.Name,
                "concurrent-reviewer",
                StringComparison.OrdinalIgnoreCase));
        matchingRoles.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateRoleAsync_NameSemantics_PreserveSameNameAndRejectCaseOnlyRename()
    {
        var store = new InMemoryRoleStore();
        var created = await store.CreateRoleAsync(new RoleDefinition
        {
            Name = "immutable-reviewer",
            Description = "Original description",
        });
        var sameNameUpdate = new RoleDefinition
        {
            RoleId = created.RoleId,
            Name = created.Name,
            Description = "Updated description",
            Permissions = created.Permissions,
            CreatedAt = created.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var updated = await store.UpdateRoleAsync(sameNameUpdate);

        updated.Should().NotBeNull();
        updated!.Description.Should().Be("Updated description");

        var caseOnlyRename = () => store.UpdateRoleAsync(new RoleDefinition
        {
            RoleId = created.RoleId,
            Name = created.Name.ToUpperInvariant(),
            Description = "Should not be stored",
            Permissions = created.Permissions,
            CreatedAt = created.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await caseOnlyRename.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be changed*");

        var stored = await store.GetRoleAsync(created.RoleId);
        stored!.Name.Should().Be("immutable-reviewer");
        stored.Description.Should().Be("Updated description");
    }

    [Fact]
    public async Task DeleteRoleAsync_TombstonesNameAndRemovesEffectivePermissions()
    {
        var store = new InMemoryRoleStore();
        var created = await store.CreateRoleAsync(new RoleDefinition
        {
            Name = "deleted-reviewer",
            Permissions =
            [
                new PermissionGrant { Service = "*", Layer = "*", Operation = "query" },
            ],
        });

        (await store.DeleteRoleAsync(created.RoleId)).Should().BeTrue();
        (await store.GetRoleAsync(created.RoleId)).Should().BeNull();
        (await store.GetEffectivePermissionsAsync("stale-user", [created.Name]))
            .Permissions.Should().BeEmpty();

        var recreate = () => store.CreateRoleAsync(new RoleDefinition
        {
            Name = created.Name.ToUpperInvariant(),
        });
        await recreate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }
}
