// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the scoped admin API-key permission grammar (#1985). These are
/// pure, deterministic tests of <see cref="AdminApiKeyPermission"/> and do not
/// require a database or Docker.
/// </summary>
public sealed class AdminApiKeyPermissionTests
{
    private static ClaimsPrincipal AdminPrincipal(params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, "admin") };
        foreach (var permission in permissions)
        {
            claims.Add(new Claim(AdminApiKeyPermission.PermissionClaimType, permission));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("*")]
    [InlineData("admin:*")]
    [InlineData("admin:write")]
    [InlineData("admin:manage")]
    public void ResolveAccessLevel_WriteCapableGrant_IsWrite(string grant)
    {
        var level = AdminApiKeyPermission.ResolveAccessLevel(AdminPrincipal(grant));

        level.Should().Be(AdminApiKeyPermission.AdminAccessLevel.Write);
    }

    [Fact]
    public void ResolveAccessLevel_ReadGrant_IsRead()
    {
        var level = AdminApiKeyPermission.ResolveAccessLevel(AdminPrincipal("admin:read"));

        level.Should().Be(AdminApiKeyPermission.AdminAccessLevel.Read);
    }

    [Fact]
    public void ResolveAccessLevel_ApproveGrant_IsReadAndDistinct()
    {
        var principal = AdminPrincipal("admin:read", "admin:approve");

        AdminApiKeyPermission.ResolveAccessLevel(principal)
            .Should().Be(AdminApiKeyPermission.AdminAccessLevel.Read);
        AdminApiKeyPermission.HasApproveGrant(principal).Should().BeTrue();
        AdminApiKeyPermission.IsAuthorized(principal, "PUT").Should().BeFalse();
    }

    [Fact]
    public void ResolveAccessLevel_NoPermissionClaimsButAdminRole_IsWrite()
    {
        // Bootstrap password / client certificate / dev-bypass: admin role, no
        // scoped permission claims => full admin, preserving legacy behavior.
        var level = AdminApiKeyPermission.ResolveAccessLevel(AdminPrincipal());

        level.Should().Be(AdminApiKeyPermission.AdminAccessLevel.Write);
    }

    [Fact]
    public void ResolveAccessLevel_OnlyNonAdminGrant_IsNone()
    {
        // A layer-scoped write: grant confers no admin authority.
        var level = AdminApiKeyPermission.ResolveAccessLevel(AdminPrincipal("write:roads"));

        level.Should().Be(AdminApiKeyPermission.AdminAccessLevel.None);
    }

    [Fact]
    public void ResolveAccessLevel_MixedGrants_TakesHighest()
    {
        var level = AdminApiKeyPermission.ResolveAccessLevel(AdminPrincipal("admin:read", "admin:write"));

        level.Should().Be(AdminApiKeyPermission.AdminAccessLevel.Write);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void IsAuthorized_ReadKey_AllowsSafeMethods(string method)
    {
        AdminApiKeyPermission.IsAuthorized(AdminPrincipal("admin:read"), method).Should().BeTrue();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void IsAuthorized_ReadKey_DeniesMutatingMethods(string method)
    {
        AdminApiKeyPermission.IsAuthorized(AdminPrincipal("admin:read"), method).Should().BeFalse();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void IsAuthorized_FullAdminKey_AllowsEveryMethod(string method)
    {
        AdminApiKeyPermission.IsAuthorized(AdminPrincipal("admin:*"), method).Should().BeTrue();
    }

    [Fact]
    public void IsAuthorized_KeyWithoutAdminGrant_DeniesEvenSafeMethods()
    {
        AdminApiKeyPermission.IsAuthorized(AdminPrincipal("write:roads"), "GET").Should().BeFalse();
    }

    [Fact]
    public void IsAuthorized_ApprovedOperationGrant_AllowsOnlyExactMethodAndPath()
    {
        var grant = AdminApiKeyPermission.CreateApprovedOperationGrant(
            "POST", "/api/v1/admin/connections");
        var principal = AdminPrincipal(grant);

        AdminApiKeyPermission.ResolveAccessLevel(principal)
            .Should().Be(AdminApiKeyPermission.AdminAccessLevel.None);
        AdminApiKeyPermission.IsAuthorized(
            principal, "POST", "/api/v1/admin/connections").Should().BeTrue();
        AdminApiKeyPermission.IsAuthorized(
            principal, "PUT", "/api/v1/admin/connections").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(
            principal, "POST", "/api/v1/admin/users").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(
            principal, "GET", "/api/v1/admin/connections").Should().BeFalse();
    }

    [Theory]
    [InlineData("/healthz/metrics")]
    [InlineData("/metrics")]
    [InlineData("/ogc/features/collections")]
    [InlineData("/")]
    public void IsAuthorized_NonAdminPath_DoesNotThrow(string path)
    {
        // The approved-operation grant factory throws for non-admin paths, and
        // IsAuthorized runs for every authenticated request: computing the grant
        // unconditionally converted ALL non-admin traffic into 400s (trunk red at
        // bf7ce4956, run 33333044607). Non-admin paths must fall through to the
        // ordinary access-level rules without touching the grant vocabulary.
        var principal = AdminPrincipal("admin:read");

        var act = () => AdminApiKeyPermission.IsAuthorized(principal, "GET", path);

        act.Should().NotThrow();
        act().Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("admin")]
    [InlineData("*")]
    [InlineData("admin:*")]
    public void HasCredentialManagementAuthority_FullAdminGrant_IsTrue(string grant)
    {
        AdminApiKeyPermission.HasCredentialManagementAuthority(AdminPrincipal(grant), "POST", "/api/v1/admin/api-keys")
            .Should().BeTrue();
    }

    [UnitTest]
    public void HasCredentialManagementAuthority_AdminRoleWithoutGrants_IsTrue()
    {
        // Bootstrap password, client certificate and human admin sessions authenticate
        // with the admin role and no permission claims; they keep credential authority.
        AdminApiKeyPermission.HasCredentialManagementAuthority(AdminPrincipal(), "POST", "/api/v1/admin/api-keys")
            .Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("admin:write")]
    [InlineData("admin:manage")]
    [InlineData("admin:read")]
    [InlineData("admin:approve")]
    public void HasCredentialManagementAuthority_ScopedAdminGrant_IsFalse(string grant)
    {
        // Scoped admin grants (including admin:write, which resolves to general admin write)
        // do not carry credential-management authority.
        AdminApiKeyPermission.HasCredentialManagementAuthority(AdminPrincipal(grant), "POST", "/api/v1/admin/api-keys")
            .Should().BeFalse();
    }

    [UnitTest]
    public void HasCredentialManagementAuthority_InstallerOperationalKey_IsFalse()
    {
        var installerKey = AdminPrincipal("admin:read", "admin:write", "read:*");

        AdminApiKeyPermission.IsFullAdminPrincipal(installerKey).Should().BeTrue("admin:write is general admin write");
        AdminApiKeyPermission.HasCredentialManagementAuthority(installerKey, "POST", "/api/v1/admin/api-keys")
            .Should().BeFalse();
    }

    [UnitTest]
    public void HasCredentialManagementAuthority_ExactApprovedOperationGrant_IsTrueOnlyForThatOperation()
    {
        var grants = AdminApiKeyPermission.CreateApprovedOperationGrants("POST", "/api/v1/admin/api-keys", "public");
        var claims = grants.Select(grant => new Claim(AdminApiKeyPermission.PermissionClaimType, grant))
            .Append(new Claim(ClaimTypes.Role, AdminApiKeyPermission.ApprovedOperationRole));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        AdminApiKeyPermission.HasCredentialManagementAuthority(principal, "POST", "/api/v1/admin/api-keys")
            .Should().BeTrue();
        AdminApiKeyPermission.HasCredentialManagementAuthority(principal, "PUT", "/api/v1/admin/users/user-1/roles")
            .Should().BeFalse();
    }

    [UnitTest]
    public void HasCredentialManagementAuthority_NonAdminPrincipalWithoutGrants_IsFalse()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "viewer")], authenticationType: "Test"));

        AdminApiKeyPermission.HasCredentialManagementAuthority(principal, "POST", "/api/v1/admin/api-keys")
            .Should().BeFalse();
    }
}
