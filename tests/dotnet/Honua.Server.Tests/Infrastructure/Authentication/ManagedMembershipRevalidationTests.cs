// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Deprovision and role edits apply to the next request. A managed user does not keep
/// the admin role captured in a token or session.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ManagedMembershipRevalidationTests
{
    [UnitTest]
    public async Task OidcTransformation_ReplacesRolesAndRejectsDeprovisionedUsers()
    {
        var users = new InMemoryUserStore();
        var created = await users.CreateUserAsync(
            new ScimUserProvisioning
            {
                UserName = "ada",
                ExternalId = "ada-sub",
                DisplayName = "Ada",
                Roles = ["admin"],
            },
            CancellationToken.None);
        Assert.NotNull(created);
        var source = new ManagedUserPrincipalMembershipSource(users);
        var transformation = new OidcClaimsTransformation(
            Options.Create(new OidcAuthenticationOptions()),
            NullLogger<OidcClaimsTransformation>.Instance,
            new ServiceCollection().BuildServiceProvider(),
            source);

        var active = await transformation.TransformAsync(Principal());
        Assert.True(active.Identity!.IsAuthenticated);
        Assert.True(active.IsInRole("admin"));

        await users.UpdateUserRolesAsync("ada", ["viewer"], CancellationToken.None);
        var demoted = await transformation.TransformAsync(Principal());
        Assert.True(demoted.Identity!.IsAuthenticated);
        Assert.True(demoted.IsInRole("viewer"));
        Assert.False(demoted.IsInRole("admin"));

        Assert.True(await users.DeprovisionUserAsync("ada", CancellationToken.None));
        var deprovisioned = await transformation.TransformAsync(Principal());
        Assert.False(deprovisioned.Identity!.IsAuthenticated);
        Assert.False(deprovisioned.IsInRole("admin"));
    }

    [UnitTest]
    public async Task PortalTokenTransformation_DropsRolesWhenTheSourceFails()
    {
        var source = new ThrowingMembership();
        var transformation = new ManagedMembershipClaimsTransformation(source);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "ada-sub"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "portal-token"),
            ],
            authenticationType: "PortalToken");

        var result = await transformation.TransformAsync(new ClaimsPrincipal(identity));

        Assert.False(result.Identity!.IsAuthenticated);
        Assert.False(result.IsInRole("admin"));
    }

    private static ClaimsPrincipal Principal()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "ada-sub"),
                new Claim(ClaimTypes.Role, "admin"),
            ],
            authenticationType: "oidc");
        return new ClaimsPrincipal(identity);
    }

    private sealed class ThrowingMembership : IPrincipalMembershipSource
    {
        public Task<PrincipalMembership?> ResolveMembershipAsync(string principalId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("membership store unavailable");

        public Task<PrincipalMembership?> ResolveMembershipAsync(
            string principalId,
            string? issuer,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("membership store unavailable");
    }
}
