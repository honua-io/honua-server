// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the pure attenuation math that computes and freezes the
/// narrow-only intersection at the heart of the custom-code auth spine. These prove
/// the intersection can only narrow, before any token or pipeline is involved.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class ScopedJobAttenuationTests
{
    [UnitTest]
    public void Intersect_DropsResourcesOwnerCannotReach()
    {
        var effective = ScopedJobAttenuation.Intersect(
            ownerRoles: ["data-editor:parcels"],
            ownerGrants: null,
            globalDataEditorRoles: null,
            requestedScope:
            [
                new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write),
                new JobResourceScopeEntry("zoning", null, JobResourceAccess.Write),
            ]);

        effective.Should().ContainSingle();
        effective[0].ServiceId.Should().Be("parcels");
        effective[0].Access.Should().Be(JobResourceAccess.Write);
    }

    [UnitTest]
    public void Intersect_ClampsRequestedWriteDownToOwnerReadAccess()
    {
        var effective = ScopedJobAttenuation.Intersect(
            ownerRoles: [],
            ownerGrants: ["read:zoning"],
            globalDataEditorRoles: null,
            requestedScope: [new JobResourceScopeEntry("zoning", null, JobResourceAccess.Write)]);

        effective.Should().ContainSingle();
        effective[0].Access.Should().Be(JobResourceAccess.Read);
    }

    [UnitTest]
    public void Intersect_AdminOwner_StillBoundedByRequestedScope()
    {
        var effective = ScopedJobAttenuation.Intersect(
            ownerRoles: ["admin"],
            ownerGrants: null,
            globalDataEditorRoles: null,
            requestedScope: [new JobResourceScopeEntry("parcels", "lots", JobResourceAccess.Read)]);

        // Admin can reach everything, but the scope ceiling is a single read-only layer.
        effective.Should().ContainSingle();
        effective[0].ServiceId.Should().Be("parcels");
        effective[0].LayerId.Should().Be("lots");
        effective[0].Access.Should().Be(JobResourceAccess.Read);
    }

    [UnitTest]
    public void Intersect_ServiceWideRequestNotSatisfiedByLayerGrant()
    {
        // A single-layer grant must NOT widen to a service-wide entry.
        var effective = ScopedJobAttenuation.Intersect(
            ownerRoles: [],
            ownerGrants: ["write:parcels/lots"],
            globalDataEditorRoles: null,
            requestedScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write)]);

        effective.Should().BeEmpty();
    }

    [UnitTest]
    public void Intersect_GlobalDataEditorRole_ReachesAnyService()
    {
        var effective = ScopedJobAttenuation.Intersect(
            ownerRoles: ["gis-editors"],
            ownerGrants: null,
            globalDataEditorRoles: ["gis-editors"],
            requestedScope: [new JobResourceScopeEntry("anything", null, JobResourceAccess.Write)]);

        effective.Should().ContainSingle();
        effective[0].Access.Should().Be(JobResourceAccess.Write);
    }

    [UnitTest]
    public void IsWithinOwner_FlagsFirstUnreachableEntry()
    {
        var within = ScopedJobAttenuation.IsWithinOwner(
            ownerRoles: ["data-editor:parcels"],
            ownerGrants: null,
            globalDataEditorRoles: null,
            requestedScope:
            [
                new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write),
                new JobResourceScopeEntry("zoning", null, JobResourceAccess.Read),
            ],
            out var unreachable);

        within.Should().BeFalse();
        unreachable.Should().NotBeNull();
        unreachable!.ServiceId.Should().Be("zoning");
    }

    [UnitTest]
    public void IsWithinOwner_WriteRequestAgainstReadOwner_IsNotWithin()
    {
        var within = ScopedJobAttenuation.IsWithinOwner(
            ownerRoles: [],
            ownerGrants: ["read:zoning"],
            globalDataEditorRoles: null,
            requestedScope: [new JobResourceScopeEntry("zoning", null, JobResourceAccess.Write)],
            out var unreachable);

        within.Should().BeFalse();
        unreachable!.ServiceId.Should().Be("zoning");
    }

    [UnitTest]
    public void IsWithinOwner_ExactReach_IsWithin()
    {
        var within = ScopedJobAttenuation.IsWithinOwner(
            ownerRoles: ["data-editor:parcels"],
            ownerGrants: ["read:zoning"],
            globalDataEditorRoles: null,
            requestedScope:
            [
                new JobResourceScopeEntry("parcels", "lots", JobResourceAccess.Write),
                new JobResourceScopeEntry("zoning", null, JobResourceAccess.Read),
            ],
            out var unreachable);

        within.Should().BeTrue();
        unreachable.Should().BeNull();
    }

    [UnitTest]
    public void ToClaims_ServiceWideWrite_ProjectsRoleAndBothGrants()
    {
        var claims = ScopedJobAttenuation.ToClaims(
            [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write)]);

        claims.Roles.Should().Contain("data-editor:parcels");
        claims.Permissions.Should().Contain(["read:parcels", "write:parcels"]);
    }

    [UnitTest]
    public void ToClaims_LayerWrite_DoesNotProjectServiceRole()
    {
        var claims = ScopedJobAttenuation.ToClaims(
            [new JobResourceScopeEntry("parcels", "lots", JobResourceAccess.Write)]);

        // A layer-specific write must not grant the whole-service editor role.
        claims.Roles.Should().BeEmpty();
        claims.Permissions.Should().Contain(["read:parcels/lots", "write:parcels/lots"]);
    }

    [UnitTest]
    public void ToClaims_ReadEntry_ProjectsReadGrantOnly()
    {
        var claims = ScopedJobAttenuation.ToClaims(
            [new JobResourceScopeEntry("zoning", null, JobResourceAccess.Read)]);

        claims.Roles.Should().BeEmpty();
        claims.Permissions.Should().ContainSingle().Which.Should().Be("read:zoning");
    }
}
