// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Import.FileImport;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[SecurityTest]
public sealed class ImportOperationAuthorizationTests
{
    [UnitTheory]
    [InlineData(false, false, "tenant-a", "tenant-a", false, true)]
    [InlineData(false, false, "tenant-a", "tenant-b", false, false)]
    [InlineData(false, true, "tenant-a", "tenant-b", false, true)]
    [InlineData(false, false, "tenant-a", null, false, false)]
    [InlineData(false, false, null, null, false, true)]
    [InlineData(false, true, "tenant-a", null, false, true)]
    [InlineData(true, false, "tenant-a", "tenant-a", true, true)]
    [InlineData(true, false, "tenant-a", "tenant-a", false, false)]
    [InlineData(true, true, "tenant-a", "tenant-b", true, false)]
    [InlineData(true, true, "tenant-a", "tenant-a", false, false)]
    [InlineData(true, false, "tenant-a", null, true, false)]
    [InlineData(true, false, null, null, true, false)]
    public async Task Access_EnforcesPersistedOwnershipAndTenant(
        bool agent, bool platformAdmin, string? currentTenant, string? storedTenant, bool sameActor, bool allowed)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(currentTenant);
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("agent", ["agent:workspace", "admin:read"], null, null, default);
        using var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(tenant)
            .AddSingleton<IAdminApiKeyStore>(keys).BuildServiceProvider();
        var identity = agent ? (ClaimsIdentity)ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record).Identity!
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test");
        if (platformAdmin) identity.AddClaim(new Claim(ClaimTypes.Role, "platform_admin"));
        var context = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity) };
        // Caller-supplied workspace headers cannot grant access to another actor's job.
        context.Request.Headers["X-Honua-Workspace"] = "other-workspace";
        context.Request.Headers["X-Honua-Turn"] = "other-turn";
        var owner = sameActor ? CanonicalSecurityActor.Resolve(context.User)!.ActorId : "another-actor";
        var ownership = new ImportOperationOwnership(owner, storedTenant);

        (await ImportOperationAuthorization.CanAccessAsync(context, ownership, default)).Should().Be(allowed);
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        (await ImportOperationAuthorization.CanAccessAsync(context, ownership, default)).Should().BeFalse();
    }

    [UnitTest]
    public async Task LegacyOwnership_IsNeverInferredForAnAgent()
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("agent", ["agent:workspace", "admin:read"], null, null, default);
        using var services = new ServiceCollection().AddSingleton(authorization).AddSingleton<IAdminApiKeyStore>(keys).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record),
        };
        (await ImportOperationAuthorization.CanAccessAsync(context, null, default)).Should().BeFalse();
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        ImportOperationAuthorization.Capture(context).Should().BeNull();
        (await ImportOperationAuthorization.CanAccessAsync(context, null, default)).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPrincipal_AfterRevocationOrRotation_CannotReadUploadOrJobOrMutate(bool rotate)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("agent", ["agent:workspace", "admin:read", "admin:write"], null, null, default);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("public");
        using var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(tenant)
            .AddSingleton<IAdminApiKeyStore>(keys).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record),
        };
        context.Request.Path = "/api/v1/admin/import";
        var owner = ImportOperationAuthorization.Capture(context);
        (await ImportOperationAuthorization.CanAccessAsync(context, owner, default)).Should().BeTrue();
        (await ImportUploadScope.CaptureCurrentAsync(context, default)).Should().NotBeNull();
        (await ImportAdminAuthorization.IsAuthorizedAsync(context, context.User, default)).Should().BeTrue();
        if (rotate) await keys.RotateAsync(created.Record.Id, default);
        else await keys.RevokeAsync(created.Record.Id, default);

        // The retained authenticated principal still has the original grants; only
        // a live credential-version check can observe this authority change.
        (await ImportOperationAuthorization.CanAccessAsync(context, owner, default)).Should().BeFalse();
        (await ImportUploadScope.CaptureCurrentAsync(context, default)).Should().BeNull();
        (await ImportAdminAuthorization.IsAuthorizedAsync(context, context.User, default)).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAccess_RejectsRetainedOidcPrincipalAfterManagedMembershipRemoval(bool removedRoleOnly)
    {
        var profile = new OidcDelegatedAgentProfile { Id = "worker", Generation = 1, Enabled = true,
            Issuer = "https://workload.example", Audience = "honua-workloads", Subject = "worker", Tenant = "public",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Permissions = ["agent:workspace", "admin:read", "admin:write"] };
        var options = Substitute.For<IOptionsMonitor<OidcAuthenticationOptions>>();
        options.CurrentValue.Returns(new OidcAuthenticationOptions { Enabled = true, DelegatedAgents = [profile],
            DelegatedAgentAudiences = [new() { Issuer = profile.Issuer, Audience = profile.Audience }] });
        var members = Substitute.For<IPrincipalMembershipSource>();
        members.ResolveMembershipAsync(profile.Subject, profile.Issuer, Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["worker"]));
        var roles = Substitute.For<IRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "worker", Roles = ["worker"], Permissions = [new() { Service = "agent", Layer = "*", Operation = "workspace" },
                new() { Service = "admin", Layer = "*", Operation = "read" },
                new() { Service = "admin", Layer = "*", Operation = "write" }] });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("public");
        using var services = new ServiceCollection().AddSingleton(options).AddSingleton(members).AddSingleton(roles)
            .AddSingleton(authorization).AddSingleton(tenant).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new("iss", profile.Issuer), new("sub", profile.Subject), new("aud", profile.Audience),
            new("tenant_id", profile.Tenant), new("scope", "agent:workspace"),
            new("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))],
            OidcAuthenticationExtensions.JwtBearerScheme));
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!, services);
        await AgentCredentialAuthority.BindOidcAsync(principal, services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeTrue();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.Path = "/api/v1/admin/import";
        var ownership = ImportOperationAuthorization.Capture(context);
        (await ImportOperationAuthorization.CanAccessAsync(context, ownership, default)).Should().BeTrue();
        (await ImportUploadScope.CaptureCurrentAsync(context, default)).Should().NotBeNull();
        (await ImportAdminAuthorization.IsAuthorizedAsync(context, principal, default)).Should().BeTrue();
        members.ResolveMembershipAsync(profile.Subject, profile.Issuer, Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(removedRoleOnly, []));
        (await ImportOperationAuthorization.CanAccessAsync(context, ownership, default)).Should().BeFalse();
        (await ImportUploadScope.CaptureCurrentAsync(context, default)).Should().BeNull();
        (await ImportAdminAuthorization.IsAuthorizedAsync(context, principal, default)).Should().BeFalse();
    }
}
