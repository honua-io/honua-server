// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.WorkflowPackages;

[SecurityTest]
public sealed class WorkflowPackageCredentialStatusTests
{
    [UnitTest]
    public async Task Status_RejectsRetainedPrincipalAfterStoredKeyRevocation()
    {
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("agent", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).BuildServiceProvider();
        await keys.RevokeAsync(created.Record.Id, default);
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        // No operation store is registered: a revoked credential must be refused
        // before any persisted workflow operation is read.
        (await WorkflowPackageOperationStatus.ReadAsync(context, "workspace", "operation", default)).Should().BeNull();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_RejectsRetainedOidcPrincipalAfterManagedMembershipRemoval(bool removedRoleOnly)
    {
        var profile = new OidcDelegatedAgentProfile
        {
            Id = "worker",
            Generation = 1,
            Enabled = true,
            Issuer = "https://workload.example",
            Audience = "honua-workloads",
            Subject = "worker",
            Tenant = "public",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Permissions = ["agent:workspace"]
        };
        var options = Substitute.For<IOptionsMonitor<OidcAuthenticationOptions>>();
        options.CurrentValue.Returns(new OidcAuthenticationOptions
        {
            Enabled = true,
            DelegatedAgents = [profile],
            DelegatedAgentAudiences = [new() { Issuer = profile.Issuer, Audience = profile.Audience }]
        });
        var members = Substitute.For<IPrincipalMembershipSource>();
        members.ResolveMembershipAsync(profile.Subject, profile.Issuer, Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["worker"]));
        var roles = Substitute.For<IRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "worker", Roles = ["worker"], Permissions = [new() { Service = "agent", Layer = "*", Operation = "workspace" }] });
        using var services = new ServiceCollection().AddSingleton(options).AddSingleton(members).AddSingleton(roles).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new("iss", profile.Issuer), new("sub", profile.Subject), new("aud", profile.Audience),
            new("tenant_id", profile.Tenant), new("scope", "agent:workspace"),
            new("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))],
            OidcAuthenticationExtensions.JwtBearerScheme));
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!, services);
        await AgentCredentialAuthority.BindOidcAsync(principal, services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeTrue();
        members.ResolveMembershipAsync(profile.Subject, profile.Issuer, Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(removedRoleOnly, []));
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        (await WorkflowPackageOperationStatus.ReadAsync(context, "workspace", "operation", default)).Should().BeNull();
    }
}
