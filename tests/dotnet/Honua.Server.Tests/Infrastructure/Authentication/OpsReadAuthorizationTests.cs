// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Xunit;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the read-only ops-reader permission grammar (A12). Pure, deterministic tests of
/// <see cref="AdminApiKeyPermission.HasOpsReadGrant"/> and
/// <see cref="AdminApiKeyPermission.IsOpsReadAuthorized"/> — no database or Docker.
/// </summary>
public sealed class OpsReadAuthorizationTests
{
    private static ClaimsPrincipal Principal(string? role, params string[] permissions)
    {
        var claims = new List<Claim>();
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var permission in permissions)
        {
            claims.Add(new Claim(AdminApiKeyPermission.PermissionClaimType, permission));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Theory]
    [InlineData("ops:read")]
    [InlineData("ops:reader")]
    [InlineData("ops:*")]
    [InlineData("OPS:READ")]
    public void HasOpsReadGrant_OpsGrant_IsTrue(string grant)
        => AdminApiKeyPermission.HasOpsReadGrant(Principal("scoped-api-key", grant)).Should().BeTrue();

    [Theory]
    [InlineData("admin:read")]
    [InlineData("write:svc/layer")]
    [InlineData("read:layers")]
    public void HasOpsReadGrant_NonOpsGrant_IsFalse(string grant)
        => AdminApiKeyPermission.HasOpsReadGrant(Principal("scoped-api-key", grant)).Should().BeFalse();

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void IsOpsReadAuthorized_OpsReadKey_SafeMethod_IsAuthorized(string method)
        => AdminApiKeyPermission.IsOpsReadAuthorized(Principal("scoped-api-key", "ops:read"), method)
            .Should().BeTrue();

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void IsOpsReadAuthorized_OpsReadKey_MutatingMethod_IsDenied(string method)
        => AdminApiKeyPermission.IsOpsReadAuthorized(Principal("scoped-api-key", "ops:read"), method)
            .Should().BeFalse();

    [Fact]
    public void IsOpsReadAuthorized_AdminReadKey_SafeMethod_IsAuthorized()
        => AdminApiKeyPermission.IsOpsReadAuthorized(Principal("admin", "admin:read"), "GET")
            .Should().BeTrue();

    [Fact]
    public void IsOpsReadAuthorized_AdminReadKey_MutatingMethod_IsDenied()
        => AdminApiKeyPermission.IsOpsReadAuthorized(Principal("admin", "admin:read"), "POST")
            .Should().BeFalse();

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void IsOpsReadAuthorized_FullAdmin_AnyMethod_IsAuthorized(string method)
        => AdminApiKeyPermission.IsOpsReadAuthorized(Principal("admin", "admin:*"), method)
            .Should().BeTrue();

    [Fact]
    public void IsOpsReadAuthorized_BootstrapAdmin_NoClaims_AnyMethod_IsAuthorized()
    {
        // Bootstrap password / client cert: admin role, no permission claims => full admin write.
        AdminApiKeyPermission.IsOpsReadAuthorized(Principal("admin"), "POST").Should().BeTrue();
    }

    [Fact]
    public void IsOpsReadAuthorized_NonOpsNonAdminKey_IsDenied()
    {
        // A layer-scoped write key carries no admin or ops grant: denied even on safe reads.
        AdminApiKeyPermission.IsOpsReadAuthorized(Principal("layer-write-key", "write:svc/layer"), "GET")
            .Should().BeFalse();
    }
}
