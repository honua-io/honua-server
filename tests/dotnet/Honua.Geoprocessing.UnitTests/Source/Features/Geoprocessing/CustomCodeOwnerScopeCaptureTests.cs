// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the submit-time owner-snapshot capture (Phase 0 auth spine,
/// Deliverable 1): it pins the submitter's tenant/roles/declared scope, validates
/// the declared scope is ⊆ the submitter, and preserves existing behavior for jobs
/// that declare no scope.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class CustomCodeOwnerScopeCaptureTests
{
    [UnitTest]
    public void TryCapture_NoDeclaredScope_ReturnsNull_PreservesBehavior()
    {
        var principal = Principal("alice", tenant: "tenant-A", roles: ["data-editor:parcels"]);

        var snapshot = CustomCodeOwnerScopeCapture.TryCapture(
            principal,
            protocolMetadata: new Dictionary<string, string>(),
            globalDataEditorRoles: null,
            out var rejection);

        snapshot.Should().BeNull();
        rejection.Should().BeNull();
    }

    [UnitTest]
    public void TryCapture_DeclaredScopeWithinOwner_PinsSnapshot()
    {
        var principal = Principal(
            "alice",
            tenant: "tenant-A",
            roles: ["data-editor:parcels"],
            permissions: ["read:zoning"]);

        var metadata = new Dictionary<string, string>
        {
            [CustomCodeOwnerScopeCapture.DeclaredScopeMetadataKey] = "write:parcels;read:zoning",
        };

        var snapshot = CustomCodeOwnerScopeCapture.TryCapture(
            principal, metadata, globalDataEditorRoles: null, out var rejection);

        rejection.Should().BeNull();
        snapshot.Should().NotBeNull();
        snapshot!.PrincipalId.Should().Be("alice");
        snapshot.TenantId.Should().Be("tenant-A");
        snapshot.Roles.Should().Contain("data-editor:parcels");
        snapshot.DeclaredScope.Should().HaveCount(2);
        snapshot.DeclaredScope.Should().Contain(e =>
            e.ServiceId == "parcels" && e.LayerId == null && e.Access == JobResourceAccess.Write);
        snapshot.DeclaredScope.Should().Contain(e =>
            e.ServiceId == "zoning" && e.Access == JobResourceAccess.Read);
    }

    [UnitTest]
    public void TryCapture_DeclaredScopeExceedsOwner_Rejects()
    {
        // Submitter can only reach 'parcels'; declaring 'secret' exceeds them.
        var principal = Principal("alice", tenant: null, roles: ["data-editor:parcels"]);

        var metadata = new Dictionary<string, string>
        {
            [CustomCodeOwnerScopeCapture.DeclaredScopeMetadataKey] = "write:parcels;write:secret",
        };

        var snapshot = CustomCodeOwnerScopeCapture.TryCapture(
            principal, metadata, globalDataEditorRoles: null, out var rejection);

        snapshot.Should().BeNull();
        rejection.Should().NotBeNull();
        rejection.Should().Contain("secret");
    }

    [UnitTest]
    public void TryCapture_WriteDeclaredAgainstReadOnlyOwner_Rejects()
    {
        // Submitter can only read 'zoning'; declaring write exceeds them.
        var principal = Principal("alice", tenant: null, roles: [], permissions: ["read:zoning"]);

        var metadata = new Dictionary<string, string>
        {
            [CustomCodeOwnerScopeCapture.DeclaredScopeMetadataKey] = "write:zoning",
        };

        var snapshot = CustomCodeOwnerScopeCapture.TryCapture(
            principal, metadata, globalDataEditorRoles: null, out var rejection);

        snapshot.Should().BeNull();
        rejection.Should().NotBeNull();
    }

    [UnitTest]
    public void TryCapture_GrantBasedWriteSubmitter_PinsGrantsAndAcceptsScope()
    {
        // #2192: the submitter's write authority comes purely from a layer-scoped
        // write:{service}/{layer} permission GRANT — no data-editor role. The capture
        // must accept the declared write AND pin the grant into the snapshot so the
        // later mint can intersect against it (instead of under-scoping the token).
        var principal = Principal(
            "dana",
            tenant: "tenant-A",
            roles: [],
            permissions: ["write:parcels/lots"]);

        var metadata = new Dictionary<string, string>
        {
            [CustomCodeOwnerScopeCapture.DeclaredScopeMetadataKey] = "write:parcels/lots",
        };

        var snapshot = CustomCodeOwnerScopeCapture.TryCapture(
            principal, metadata, globalDataEditorRoles: null, out var rejection);

        rejection.Should().BeNull();
        snapshot.Should().NotBeNull();
        snapshot!.Roles.Should().BeEmpty();
        snapshot.Grants.Should().Contain("write:parcels/lots");
        snapshot.DeclaredScope.Should().ContainSingle().Which.Should().Match<JobResourceScopeEntry>(e =>
            e.ServiceId == "parcels" && e.LayerId == "lots" && e.Access == JobResourceAccess.Write);
    }

    [UnitTest]
    public void TryCapture_TenantPinnedFromClaim_NotFromMetadata()
    {
        // Even if metadata tried to carry a tenant, the snapshot tenant comes only
        // from the principal's claim (non-forgeable, invariant #2).
        var principal = Principal("alice", tenant: "tenant-REAL", roles: ["data-editor:parcels"]);

        var metadata = new Dictionary<string, string>
        {
            [CustomCodeOwnerScopeCapture.DeclaredScopeMetadataKey] = "read:parcels",
            ["tenant_id"] = "tenant-FORGED",
        };

        var snapshot = CustomCodeOwnerScopeCapture.TryCapture(
            principal, metadata, globalDataEditorRoles: null, out _);

        snapshot.Should().NotBeNull();
        snapshot!.TenantId.Should().Be("tenant-REAL");
    }

    private static ClaimsPrincipal Principal(
        string name,
        string? tenant,
        string[] roles,
        string[]? permissions = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (!string.IsNullOrEmpty(tenant))
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var permission in permissions ?? [])
        {
            claims.Add(new Claim("permission", permission));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
