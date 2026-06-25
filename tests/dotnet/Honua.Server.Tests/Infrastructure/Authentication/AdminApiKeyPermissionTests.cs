// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
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
}
