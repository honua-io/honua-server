// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Tests.Features.Authorization;

/// <summary>
/// Unit tests for <see cref="PortalAccessProjection"/> — the shared seam (#1370)
/// that turns a canonical RBAC decision and the coarse <see cref="AccessPolicy"/>
/// into the ArcGIS Portal <see cref="PortalAccessLevel"/> and per-item visibility
/// consumed by the read surface (#1243) and OAuth2 bridge (#1242). Locks in the
/// access-level mapping, the layer-narrows-service rule, the grant-over-policy
/// visibility fold, and the off-by-default gate.
/// </summary>
public sealed class PortalAccessProjectionTests
{
    private static readonly PortalAccessProjection Enabled = new(enabled: true);

    private static PermissionDecision Allow()
        => PermissionDecision.Allow(new PermissionGrant { Service = "svc", Layer = "*", Operation = "query" });

    [Fact]
    public void IsEnabled_DefaultsToFalse_SoTheFacadeStaysOffByDefault()
    {
        var projection = new PortalAccessProjection();

        projection.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ProjectAccessLevel_AnonymousReadAllowed_IsPublic()
    {
        var policy = new AccessPolicy { AllowAnonymous = true };

        Enabled.ProjectAccessLevel(policy, null).Should().Be(PortalAccessLevel.Public);
    }

    [Fact]
    public void ProjectAccessLevel_AuthenticatedNoRoleRestriction_IsOrg()
    {
        var policy = new AccessPolicy { AllowAnonymous = false };

        Enabled.ProjectAccessLevel(policy, null).Should().Be(PortalAccessLevel.Organization);
    }

    [Fact]
    public void ProjectAccessLevel_RoleRestricted_IsPrivate()
    {
        var policy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["editor"] };

        Enabled.ProjectAccessLevel(policy, null).Should().Be(PortalAccessLevel.Private);
    }

    [Fact]
    public void ProjectAccessLevel_NoPolicyAtAll_IsPrivate()
    {
        Enabled.ProjectAccessLevel(null, null).Should().Be(PortalAccessLevel.Private);
    }

    [Fact]
    public void ProjectAccessLevel_LayerNarrowsService_TakesMoreRestrictiveLevel()
    {
        var service = new AccessPolicy { AllowAnonymous = true }; // public
        var layer = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["editor"] }; // private

        Enabled.ProjectAccessLevel(layer, service).Should().Be(PortalAccessLevel.Private);
    }

    [Fact]
    public void ProjectAccessLevel_LayerWithoutPolicy_InheritsServiceLevel()
    {
        var service = new AccessPolicy { AllowAnonymous = true };

        Enabled.ProjectAccessLevel(null, service).Should().Be(PortalAccessLevel.Public);
    }

    [Fact]
    public void ProjectVisibility_ExplicitGrant_IsVisible()
    {
        var visibility = Enabled.ProjectVisibility(Allow(), AccessDecision.Forbidden());

        visibility.IsVisible.Should().BeTrue();
    }

    [Fact]
    public void ProjectVisibility_ResolverRequiresAuth_RequestsAuthentication()
    {
        var visibility = Enabled.ProjectVisibility(
            PermissionDecision.RequiresAuthentication(),
            AccessDecision.Forbidden());

        visibility.IsVisible.Should().BeFalse();
        visibility.RequiresAuthentication.Should().BeTrue();
    }

    [Fact]
    public void ProjectVisibility_NoGrant_FallsBackToCoarseAllow()
    {
        var visibility = Enabled.ProjectVisibility(PermissionDecision.NoMatch(), AccessDecision.Allowed());

        visibility.IsVisible.Should().BeTrue();
    }

    [Fact]
    public void ProjectVisibility_NoGrantAndCoarseRequiresAuth_RequestsAuthentication()
    {
        var visibility = Enabled.ProjectVisibility(null, AccessDecision.RequiresAuth());

        visibility.IsVisible.Should().BeFalse();
        visibility.RequiresAuthentication.Should().BeTrue();
    }

    [Fact]
    public void ProjectVisibility_NoGrantAndCoarseForbidden_IsHidden()
    {
        var visibility = Enabled.ProjectVisibility(null, AccessDecision.Forbidden());

        visibility.IsVisible.Should().BeFalse();
        visibility.RequiresAuthentication.Should().BeFalse();
    }

    [Theory]
    [InlineData(PortalAccessLevel.Public, "public")]
    [InlineData(PortalAccessLevel.Organization, "org")]
    [InlineData(PortalAccessLevel.Private, "private")]
    public void ToWireName_EmitsEsriTokens(PortalAccessLevel level, string expected)
    {
        level.ToWireName().Should().Be(expected);
    }
}
