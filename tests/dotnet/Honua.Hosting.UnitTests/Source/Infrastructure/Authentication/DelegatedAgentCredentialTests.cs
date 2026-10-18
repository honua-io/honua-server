// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class DelegatedAgentCredentialTests
{
    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("subject")]
    [InlineData("tenant")]
    [InlineData("unvalidated")]
    [InlineData("expired")]
    [InlineData("multi-audience")]
    [InlineData("duplicate-subject")]
    [InlineData("duplicate-tenant")]
    public async Task Bind_RejectsMismatchedOrUnvalidatedToken(string mismatch)
    {
        using var fixture = new Fixture();
        var principal = Token(mismatch);
        if (mismatch == "duplicate-subject") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("sub", "subject"));
        if (mismatch == "duplicate-tenant") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("tenant_id", "tenant"));
        if (mismatch == "multi-audience") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("aud", "other"));
        if (mismatch != "unvalidated") AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeFalse();
    }

    [Fact]
    public async Task Replay_RejectsExpiredOriginatingToken()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        (await AgentCredentialAuthority.RevalidateAsync(snapshot! with { TokenExpiresAt = DateTimeOffset.MinValue }, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task ForgedProfileAndPermissionClaims_DoNotCaptureAuthority()
    {
        using var fixture = new Fixture();
        var principal = Token();
        ((ClaimsIdentity)principal.Identity!).AddClaims([new Claim("permission", "agent:workspace"), new Claim("honua:delegated_agent_profile", "worker")]);
        (await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task BoundProfile_RevalidatesMembershipAndRoleGrantsBeforeReplay()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        snapshot!.Kind.Should().Be("oidc-delegated-agent");
        principal.FindFirst("api_key_id").Should().BeNull();
        (await AgentCredentialAuthority.RevalidateAsync(snapshot, fixture.Services, default)).Should().NotBeNull();
        fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(false, []));
        (await AgentCredentialAuthority.RevalidateAsync(snapshot, fixture.Services, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("generation")]
    [InlineData("expired")]
    [InlineData("subject")]
    [InlineData("role")]
    [InlineData("ceiling")]
    public async Task SealedCredential_RejectsChangedCurrentAuthority(string change)
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        switch (change)
        {
            case "disabled": fixture.Profile.Enabled = false; break;
            case "generation": fixture.Profile.Generation++; break;
            case "expired": fixture.Profile.ExpiresAt = DateTimeOffset.MinValue; break;
            case "subject": fixture.Profile.Subject = "other"; break;
            case "ceiling": fixture.Profile.Permissions = []; break;
            case "role": fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                    .Returns(new EffectivePermissions { UserId = "subject", Roles = [], Permissions = [] }); break;
        }
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task Ceiling_BoundsManagedAdminAtActualResourceAndOperatorEvaluators()
    {
        using var fixture = new Fixture();
        fixture.Profile.Permissions = ["agent:workspace", "admin:read", "read:parcels/0"];
        fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["admin"]));
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["admin"], Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "admin", Layer = "*", Operation = "read" },
                new PermissionGrant { Service = "parcels", Layer = "0", Operation = "*" }] });
        var principal = Token();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        principal.IsInRole("admin").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(principal, "POST").Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeTrue();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "1", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Update)!.Value.IsAllowed.Should().BeFalse();
        var evaluator = new OperatorAuthorizationEvaluator(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RbacOptions()), NullLogger<OperatorAuthorizationEvaluator>.Instance, fixture.Services);
        (await evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest { ResourceType = OperatorResourceType.Deployment, Operation = OperatorOperation.Read })).IsAllowed.Should().BeTrue();
        (await evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest { ResourceType = OperatorResourceType.Deployment, Operation = OperatorOperation.Publish })).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task ExistingStoredApiKey_CaptureAndRevocationRemainEnforced()
    {
        using var fixture = new Fixture();
        var store = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync("key", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record.Id, created.Record.Name, created.Record.Permissions);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        await store.RevokeAsync(created.Record.Id, default);
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task QueryOnlyCanonicalRole_DoesNotGainMetadataExportOrDifferentLayer()
    {
        using var fixture = new Fixture();
        fixture.Profile.Permissions = ["agent:workspace", "read:parcels/0"];
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "parcels", Layer = "0", Operation = "read" }] });
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeTrue();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeTrue();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Metadata)!.Value.IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Export)!.Value.IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "1", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeFalse();
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "parcels", Layer = "0", Operation = "metadata" }] });
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task CurrentAuthority_RechecksOrdinaryStoredKeyWithoutAgentWorkspaceGrant()
    {
        using var fixture = new Fixture();
        var store = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync("reader", ["admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record.Id, "reader", created.Record.Permissions);
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeTrue();
        await store.RevokeAsync(created.Record.Id, default);
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeFalse();
    }

    [Fact]
    public async Task DelegatedCredential_CannotApproveAnyProposal_OrdinaryHumanCan()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var requirement = new AdminApproveRequirement();
        var delegated = new AuthorizationHandlerContext([requirement], principal, "another-agent-proposal");
        await new AdminApproveAuthorizationHandler().HandleAsync(delegated);
        delegated.HasFailed.Should().BeTrue();
        var human = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "human"));
        var allowed = new AuthorizationHandlerContext([requirement], human, "another-agent-proposal");
        await new AdminApproveAuthorizationHandler().HandleAsync(allowed);
        allowed.HasSucceeded.Should().BeTrue();
    }

    private static ClaimsPrincipal Token(string? mismatch = null) => new(new ClaimsIdentity([
        new Claim("scope", "agent:workspace"),
        new Claim("exp", (mismatch == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(10)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new Claim("iss", mismatch == "issuer" ? "https://evil.example" : "https://issuer.example"),
        new Claim("sub", mismatch == "subject" ? "other" : "subject"),
        new Claim("aud", mismatch == "audience" ? "other" : "honua"),
        new Claim("tenant_id", mismatch == "tenant" ? "other" : "tenant")], OidcAuthenticationExtensions.JwtBearerScheme));

    private sealed class Fixture : IDisposable
    {
        internal OidcDelegatedAgentProfile Profile { get; } = new()
        {
            Id = "worker", Generation = 1, Enabled = true, Issuer = "https://issuer.example", Audience = "honua",
            Subject = "subject", Tenant = "tenant", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Permissions = ["agent:workspace"]
        };
        internal IPrincipalMembershipSource Membership { get; } = Substitute.For<IPrincipalMembershipSource>();
        internal IRoleStore Roles { get; } = Substitute.For<IRoleStore>();
        internal ServiceProvider Services { get; }
        internal Fixture()
        {
            var options = Substitute.For<IOptionsMonitor<OidcAuthenticationOptions>>();
            options.CurrentValue.Returns(new OidcAuthenticationOptions { Enabled = true, DelegatedAgents = [Profile], DelegatedAgentAudiences = [new() { Issuer = "https://issuer.example", Audience = "honua" }] });
            Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
                .Returns(new PrincipalMembership(true, ["worker"]));
            Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" }] });
            Services = new ServiceCollection().AddSingleton(options).AddSingleton(Membership).AddSingleton(Roles).AddSingleton<IAdminApiKeyStore>(new InMemoryAdminApiKeyStore()).BuildServiceProvider();
        }
        public void Dispose() => Services.Dispose();
    }
}
