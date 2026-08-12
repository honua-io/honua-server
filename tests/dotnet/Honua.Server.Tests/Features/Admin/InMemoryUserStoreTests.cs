// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Server.Features.Admin.Services;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit tests for <see cref="InMemoryUserStore"/> stable-identifier resolution (#3141).
/// The single-node store must resolve a managed identity by the SCIM <c>userName</c> AND
/// by the stable external subject (SCIM <c>externalId</c> / OIDC <c>sub</c>), because
/// deferred security snapshots capture the OIDC subject while SCIM keys the record by
/// <c>userName</c>.
/// </summary>
public sealed class InMemoryUserStoreTests
{
    [Fact]
    public async Task GetUser_ByExternalId_ResolvesRecordWhoseUserNameDiffers()
    {
        var store = new InMemoryUserStore();
        await store.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "alice@example.com",
            ExternalId = "auth0|64f1c2d3e4",
            Roles = ["editor"],
        });

        var bySubject = await store.GetUserAsync("auth0|64f1c2d3e4");
        var byUserName = await store.GetUserAsync("alice@example.com");

        bySubject.Should().NotBeNull();
        byUserName.Should().NotBeNull();
        bySubject!.UserId.Should().Be(byUserName!.UserId);
        bySubject.Roles.Should().BeEquivalentTo(["editor"]);
    }

    [Fact]
    public async Task ResolveMembership_ByOidcSubject_ReflectsDeactivation()
    {
        // ManagedUserPrincipalMembershipSource resolves through IUserStore.GetUserAsync,
        // so an identity keyed by userName must revalidate (and fail closed on
        // deactivation) when the deferred snapshot carries only the OIDC subject.
        var store = new InMemoryUserStore();
        var source = new ManagedUserPrincipalMembershipSource(store);

        await store.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "worker@example.com",
            ExternalId = "sub-worker-1",
            Roles = ["workflow-author"],
        });

        var active = await source.ResolveMembershipAsync("sub-worker-1");
        active.Should().NotBeNull();
        active!.IsActive.Should().BeTrue();
        active.Roles.Should().Contain("workflow-author");

        (await store.DeprovisionUserAsync("worker@example.com")).Should().BeTrue();

        var revoked = await source.ResolveMembershipAsync("sub-worker-1");
        revoked.Should().NotBeNull();
        revoked!.IsActive.Should().BeFalse();
        revoked.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateUser_DuplicateExternalId_ReturnsNull()
    {
        var store = new InMemoryUserStore();
        (await store.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "carol@example.com",
            ExternalId = "sub-carol",
        })).Should().NotBeNull();

        (await store.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "carol.alt@example.com",
            ExternalId = "SUB-CAROL",
        })).Should().BeNull();
    }

    [Fact]
    public async Task ReplaceUser_OmittedExternalId_PreservesStoredSubject()
    {
        var store = new InMemoryUserStore();
        await store.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "erin@example.com",
            ExternalId = "sub-erin",
        });

        var replaced = await store.ReplaceUserAsync("erin@example.com", new ScimUserProvisioning
        {
            UserName = "erin@example.com",
            DisplayName = "Erin Updated",
        });

        replaced!.ExternalId.Should().Be("sub-erin");
        (await store.GetUserAsync("sub-erin")).Should().NotBeNull();
    }

    [Fact]
    public async Task ExternalId_SurvivesRoleUpdates_DeactivationAndGroupSync()
    {
        var store = new InMemoryUserStore();
        await store.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "frank@example.com",
            ExternalId = "sub-frank",
            Roles = ["viewer"],
        });

        (await store.UpdateUserRolesAsync("frank@example.com", ["editor"]))!
            .ExternalId.Should().Be("sub-frank");

        store.AddRole("frank@example.com", "surveyors");
        (await store.GetUserAsync("sub-frank"))!.Roles.Should().Contain("surveyors");

        store.RemoveRole("frank@example.com", "surveyors");
        (await store.GetUserAsync("sub-frank"))!.Roles.Should().NotContain("surveyors");

        (await store.SetActiveAsync("frank@example.com", active: false))!
            .ExternalId.Should().Be("sub-frank");

        // The deactivated identity still resolves by subject — fail-closed, not a miss.
        var deactivated = await store.GetUserAsync("sub-frank");
        deactivated!.IsActive.Should().BeFalse();
        deactivated.Roles.Should().BeEmpty();
    }
}
