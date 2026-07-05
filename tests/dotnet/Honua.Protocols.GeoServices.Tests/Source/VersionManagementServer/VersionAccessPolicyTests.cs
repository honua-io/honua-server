// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.VersionManagementServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

/// <summary>
/// Unit tests for <see cref="VersionAccessPolicy"/> — the centralized visibility and lifecycle
/// authorization helpers that fix BH3-002 (list/info), BH3-003 (inspectConflicts), and
/// BH3-004 (delete/alter/reconcile/post/session) version-access gaps.
/// </summary>
public sealed class VersionAccessPolicyTests
{
    // ---- Test data helpers -----------------------------------------------------------------

    private static GdbVersion MakeVersion(string owner, VersionAccess access) => new()
    {
        VersionId = Guid.NewGuid(),
        VersionName = $"{owner}.test",
        Owner = owner,
        Access = access,
        State = VersionState.Active,
        CommonAncestorGeneration = 0,
        BranchGeneration = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow,
    };

    // ---- IsVersionVisible — Public / Protected visibility ----------------------------------

    [UnitTest]
    public void IsVersionVisible_PublicVersion_AllCallersCanSee()
    {
        var version = MakeVersion("alice", VersionAccess.Public);

        VersionAccessPolicy.IsVersionVisible(version, callerName: "bob", isAdmin: false).Should().BeTrue(
            "Public versions are visible to any query-access caller");
        VersionAccessPolicy.IsVersionVisible(version, callerName: null, isAdmin: false).Should().BeTrue(
            "Public versions are visible to anonymous callers with service query access");
        VersionAccessPolicy.IsVersionVisible(version, callerName: "alice", isAdmin: false).Should().BeTrue(
            "Public versions are visible to their owner");
        VersionAccessPolicy.IsVersionVisible(version, callerName: "admin", isAdmin: true).Should().BeTrue(
            "Public versions are visible to administrators");
    }

    [UnitTest]
    public void IsVersionVisible_ProtectedVersion_AllCallersCanSee()
    {
        var version = MakeVersion("alice", VersionAccess.Protected);

        VersionAccessPolicy.IsVersionVisible(version, callerName: "bob", isAdmin: false).Should().BeTrue(
            "Protected versions are read-visible to any query-access caller");
        VersionAccessPolicy.IsVersionVisible(version, callerName: null, isAdmin: false).Should().BeTrue(
            "Protected versions are read-visible to anonymous callers with service query access");
    }

    // ---- IsVersionVisible — Private visibility (BH3-002) -----------------------------------

    [UnitTest]
    public void IsVersionVisible_PrivateVersion_OwnerCanSee()
    {
        var version = MakeVersion("alice", VersionAccess.Private);

        VersionAccessPolicy.IsVersionVisible(version, callerName: "alice", isAdmin: false).Should().BeTrue(
            "a Private version owner must be able to see their own version");
    }

    [UnitTest]
    public void IsVersionVisible_PrivateVersion_OwnerMatchIsCaseInsensitive()
    {
        var version = MakeVersion("Alice", VersionAccess.Private);

        VersionAccessPolicy.IsVersionVisible(version, callerName: "alice", isAdmin: false).Should().BeTrue(
            "owner name comparison must be case-insensitive");
        VersionAccessPolicy.IsVersionVisible(version, callerName: "ALICE", isAdmin: false).Should().BeTrue(
            "owner name comparison must be case-insensitive");
    }

    [UnitTest]
    public void IsVersionVisible_PrivateVersion_AdminCanSee()
    {
        var version = MakeVersion("alice", VersionAccess.Private);

        VersionAccessPolicy.IsVersionVisible(version, callerName: "admin", isAdmin: true).Should().BeTrue(
            "service administrators must be able to see all Private versions");
    }

    [UnitTest]
    public void IsVersionVisible_PrivateVersion_NonOwnerNonAdminCannotSee()
    {
        // BH3-002 core regression: before the fix, HandleListVersions returned all Private
        // versions to any query-access caller. This test pins the corrected behavior.
        var version = MakeVersion("alice", VersionAccess.Private);

        VersionAccessPolicy.IsVersionVisible(version, callerName: "bob", isAdmin: false).Should().BeFalse(
            "a non-owner non-admin must not see a Private version");
        VersionAccessPolicy.IsVersionVisible(version, callerName: null, isAdmin: false).Should().BeFalse(
            "anonymous callers must not see a Private version");
    }

    // ---- CanManageVersion — owner-only lifecycle ops (BH3-003 / BH3-004) ------------------

    [UnitTest]
    public void CanManageVersion_Owner_CanManageRegardlessOfAccessLevel()
    {
        foreach (var access in Enum.GetValues<VersionAccess>())
        {
            var version = MakeVersion("alice", access);
            VersionAccessPolicy.CanManageVersion(version, callerName: "alice", isAdmin: false).Should().BeTrue(
                $"version owner must be able to manage their own {access} version");
        }
    }

    [UnitTest]
    public void CanManageVersion_Admin_CanManageRegardlessOfAccessLevel()
    {
        foreach (var access in Enum.GetValues<VersionAccess>())
        {
            var version = MakeVersion("alice", access);
            VersionAccessPolicy.CanManageVersion(version, callerName: "admin", isAdmin: true).Should().BeTrue(
                $"admin must be able to manage any {access} version");
        }
    }

    [UnitTest]
    public void CanManageVersion_NonOwnerNonAdmin_CannotManage_PrivateVersion()
    {
        // BH3-003 / BH3-004 core regression: before the fix any write-access principal
        // could call delete/alter/reconcile/post/inspectConflicts on any version.
        var version = MakeVersion("alice", VersionAccess.Private);

        VersionAccessPolicy.CanManageVersion(version, callerName: "bob", isAdmin: false).Should().BeFalse(
            "a non-owner must not perform lifecycle operations on a Private version");
        VersionAccessPolicy.CanManageVersion(version, callerName: null, isAdmin: false).Should().BeFalse(
            "an anonymous principal must not perform lifecycle operations on a Private version");
    }

    [UnitTest]
    public void CanManageVersion_NonOwnerNonAdmin_CannotManage_PublicVersion()
    {
        // BH3-004: even a Public version's lifecycle operations are owner-or-admin-only.
        // Read access is public, but delete/alter/reconcile/post are not.
        var version = MakeVersion("alice", VersionAccess.Public);

        VersionAccessPolicy.CanManageVersion(version, callerName: "bob", isAdmin: false).Should().BeFalse(
            "a non-owner must not perform lifecycle operations on a Public version, " +
            "even though they can read it");
    }

    [UnitTest]
    public void CanManageVersion_NonOwnerNonAdmin_CannotManage_ProtectedVersion()
    {
        // BH3-004: same restriction applies to Protected versions.
        var version = MakeVersion("alice", VersionAccess.Protected);

        VersionAccessPolicy.CanManageVersion(version, callerName: "bob", isAdmin: false).Should().BeFalse(
            "a non-owner must not perform lifecycle operations on a Protected version");
    }

    [UnitTest]
    public void CanManageVersion_OwnerMatchIsCaseInsensitive()
    {
        var version = MakeVersion("Alice", VersionAccess.Private);

        VersionAccessPolicy.CanManageVersion(version, callerName: "alice", isAdmin: false).Should().BeTrue(
            "owner name comparison must be case-insensitive for lifecycle operations");
        VersionAccessPolicy.CanManageVersion(version, callerName: "ALICE", isAdmin: false).Should().BeTrue(
            "owner name comparison must be case-insensitive for lifecycle operations");
    }
}
