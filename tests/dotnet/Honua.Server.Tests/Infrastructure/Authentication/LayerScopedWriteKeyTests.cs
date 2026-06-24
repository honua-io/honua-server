// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the permission-grant classification that governs whether an API
/// key is elevated to the full <c>admin</c> role, kept as a layer-scoped write key,
/// or restricted to a genuinely-scoped non-admin principal (issue #1985 / #1637).
/// </summary>
public sealed class LayerScopedWriteKeyTests
{
    [Fact]
    public void ConfersFullAdmin_NullPermissions_ReturnsTrue()
    {
        // The password-based bootstrap admin and the development bypass authenticate
        // with null permissions and must retain full admin authority.
        LayerScopedWriteKey.ConfersFullAdmin(null).Should().BeTrue();
    }

    [Fact]
    public void ConfersFullAdmin_EmptyPermissions_ReturnsTrue()
    {
        // The key store normalizes an empty grant set to admin:*; an empty set is unscoped.
        LayerScopedWriteKey.ConfersFullAdmin([]).Should().BeTrue();
        LayerScopedWriteKey.ConfersFullAdmin(["   "]).Should().BeTrue();
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("*")]
    [InlineData("admin:*")]
    [InlineData("admin:read")]
    [InlineData("ADMIN")]
    public void ConfersFullAdmin_AdminGrants_ReturnsTrue(string grant)
    {
        LayerScopedWriteKey.ConfersFullAdmin([grant]).Should().BeTrue();
    }

    [Fact]
    public void ConfersFullAdmin_AdminGrantAmongScopedGrants_ReturnsTrue()
    {
        LayerScopedWriteKey.ConfersFullAdmin(["read:layers", "admin"]).Should().BeTrue();
    }

    [Theory]
    [InlineData("read:layers")]
    [InlineData("metadata:read")]
    [InlineData("write:demo/parcels")]
    [InlineData("deploy:approve")]
    public void ConfersFullAdmin_GenuinelyScopedGrants_ReturnsFalse(string grant)
    {
        // A grant set that carries no full-admin grant confers no admin authority,
        // so the key must not satisfy the admin authorization policy (issue #1985).
        LayerScopedWriteKey.ConfersFullAdmin([grant]).Should().BeFalse();
    }

    [Fact]
    public void IsScopedWriteKey_WriteGrant_ReturnsTrue()
    {
        LayerScopedWriteKey.IsScopedWriteKey(["write:demo/parcels"]).Should().BeTrue();
    }

    [Fact]
    public void IsScopedWriteKey_AdminGrant_ReturnsFalse()
    {
        LayerScopedWriteKey.IsScopedWriteKey(["write:demo", "admin"]).Should().BeFalse();
    }

    [Fact]
    public void IsScopedWriteKey_ReadOnlyScopedGrant_ReturnsFalse()
    {
        // A genuinely-scoped read key is neither a write key nor an admin; it falls
        // through to the restricted non-admin principal branch.
        var permissions = new[] { "read:layers" };
        LayerScopedWriteKey.IsScopedWriteKey(permissions).Should().BeFalse();
        LayerScopedWriteKey.ConfersFullAdmin(permissions).Should().BeFalse();
    }
}
