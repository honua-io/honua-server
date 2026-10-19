// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class ManagedObserverMembershipTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("role-removed")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    public async Task RetainedManagedHuman_MustKeepCurrentMembership(string change)
    {
        var source = Substitute.For<IPrincipalMembershipSource>();
        source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["reader"]));
        var retained = await ManagedMembershipRevalidation.ApplyAsync(Human(), source);
        retained.IsInRole("reader").Should().BeTrue();
        using var services = new ServiceCollection().AddSingleton(source)
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions())).BuildServiceProvider();
        switch (change)
        {
            case "role-removed": source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>()).Returns(new PrincipalMembership(true, [])); break;
            case "inactive": source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>()).Returns(new PrincipalMembership(false, [])); break;
            case "deleted": source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>()).Returns(Task.FromResult<PrincipalMembership?>(null)); break;
        }
        (await AgentCredentialAuthority.IsCurrentObserverAsync(retained, services, default)).Should().Be(change == "unchanged");
        retained.IsInRole("reader").Should().BeTrue("validation never rewrites or substitutes the caller identity");
        OidcClaimsTransformation.IsReservedProvenanceClaimType(ManagedMembershipRevalidation.ManagedClaimType).Should().BeTrue();
    }

    [Fact]
    public async Task UnmanagedExternalHuman_KeepsConfiguredExternalRoleSemantics()
    {
        var source = Substitute.For<IPrincipalMembershipSource>();
        source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>()).Returns(Task.FromResult<PrincipalMembership?>(null));
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        var external = Human();
        ((ClaimsIdentity)external.Identity!).AddClaim(new Claim(ManagedMembershipRevalidation.ManagedClaimType, "forged"));
        (await AgentCredentialAuthority.IsCurrentObserverAsync(external, services, default)).Should().BeTrue();
    }

    [Fact]
    public async Task ManagedSourceFailure_DoesNotFallBackToRetainedRoles()
    {
        var source = Substitute.For<IPrincipalMembershipSource>();
        source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>()).Returns(new PrincipalMembership(true, ["reader"]));
        var retained = await ManagedMembershipRevalidation.ApplyAsync(Human(), source);
        source.ResolveMembershipAsync("human", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Membership store unavailable"));
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        var check = () => AgentCredentialAuthority.IsCurrentObserverAsync(retained, services, default);
        await check.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ClaimsPrincipal Human() => new(new ClaimsIdentity(
        [new Claim("sub", "human"), new Claim("iss", "https://issuer.example"), new Claim("roles", "reader")], "trusted-human-test"));
}
