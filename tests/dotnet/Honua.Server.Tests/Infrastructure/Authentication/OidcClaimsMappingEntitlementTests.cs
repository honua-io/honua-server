// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Entitlement tests for OIDC custom claims mapping (#2997). The admin provider DTOs carry no
/// claims-mapping fields, so the identity.claims-mapping (Enterprise) surface is the
/// config-driven <c>Oidc:ClaimsMapping</c> options applied by
/// <see cref="OidcClaimsTransformation"/>: without the entitlement, configured
/// <c>CustomMappings</c> and <c>AdditionalRoleClaimTypes</c> are skipped (soft-degrade — default
/// claims normalization still runs and authentication never fails on edition), and with it they
/// apply as configured. This is why the key sits in the entitlement sweep's no-http-surface
/// allowlist rather than carrying an HTTP 402 probe.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class OidcClaimsMappingEntitlementTests
{
    private static OidcClaimsTransformation CreateTransformation(HonuaEdition edition)
    {
        var options = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            ClaimsMapping = new ClaimsMappingOptions
            {
                CustomMappings = new Dictionary<string, string> { ["department"] = "honua_department" },
                AdditionalRoleClaimTypes = ["groups"],
            },
        });

        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition))
            .BuildServiceProvider();

        return new OidcClaimsTransformation(
            options,
            NullLogger<OidcClaimsTransformation>.Instance,
            services);
    }

    private static ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
        [
            new Claim("sub", "user-123"),
            new Claim("name", "Test User"),
            new Claim("department", "cartography"),
            new Claim("groups", "editors"),
        ],
        "Bearer"));

    [UnitTest]
    public async Task TransformAsync_WithoutClaimsMappingEntitlement_SkipsCustomMappings()
    {
        // Pro covers single-provider OIDC (identity.oidc) but not identity.claims-mapping.
        var transformation = CreateTransformation(HonuaEdition.Pro);

        var result = await transformation.TransformAsync(CreatePrincipal());

        Assert.Null(result.FindFirst("honua_department"));
        Assert.False(result.IsInRole("editors"));

        // Default normalization still ran: authentication soft-degrades, it never fails.
        Assert.Equal("user-123", result.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(result.IsInRole("user"));
    }

    /// <summary>
    /// Builds a transformation whose PRIMARY role claim type is a provider-specific claim,
    /// with no other custom mapping configured.
    /// </summary>
    private static OidcClaimsTransformation CreateRoleClaimTypeTransformation(HonuaEdition edition)
    {
        var options = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["platform-admins"],
            ClaimsMapping = new ClaimsMappingOptions { RoleClaimType = "groups" },
        });

        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition))
            .BuildServiceProvider();

        return new OidcClaimsTransformation(
            options,
            NullLogger<OidcClaimsTransformation>.Instance,
            services);
    }

    /// <summary>
    /// Mirrors what the JWT/OIDC handlers produce when
    /// <c>TokenValidationParameters.RoleClaimType</c> is the configured custom type: the
    /// identity itself resolves <c>IsInRole</c> against <c>groups</c>.
    /// </summary>
    private static ClaimsPrincipal CreateGroupsPrincipal() => new(new ClaimsIdentity(
        [
            new Claim("sub", "user-123"),
            new Claim("name", "Test User"),
            new Claim("groups", "platform-admins"),
        ],
        "Bearer",
        "name",
        "groups"));

    [UnitTest]
    public async Task TransformAsync_WithoutEntitlement_CustomRoleClaimTypeGrantsNoRoles()
    {
        // A non-default PRIMARY RoleClaimType is a custom mapping like any other. Without the
        // gate an unentitled Pro deployment could point it at `groups` and have raw provider
        // group values match AdminRoles (honua-server#2997 review).
        var transformation = CreateRoleClaimTypeTransformation(HonuaEdition.Pro);

        var result = await transformation.TransformAsync(CreateGroupsPrincipal());

        Assert.False(result.IsInRole("platform-admins"),
            "an ungated group value must not be read as a role");
        Assert.False(result.IsInRole("admin"),
            "and therefore must not satisfy AdminRoles");

        // Soft-degrade, not failure: the default role still applies.
        Assert.True(result.IsInRole("user"));
    }

    [UnitTest]
    public async Task TransformAsync_WithoutEntitlement_RehomesTheIdentityOntoTheDefaultRoleClaim()
    {
        // Gating the claim gathering is not enough on its own: the handlers install the custom
        // type as the identity's RoleClaimType, so IsInRole would read `groups` directly and
        // never pass through this transformation at all.
        var transformation = CreateRoleClaimTypeTransformation(HonuaEdition.Pro);

        var result = await transformation.TransformAsync(CreateGroupsPrincipal());

        Assert.Equal(ClaimTypes.Role, ((ClaimsIdentity)result.Identity!).RoleClaimType);
        // The original claim is preserved — only its role-resolving status is withheld.
        Assert.Equal("groups", result.FindFirst("groups")?.Type);
    }

    [UnitTest]
    public async Task TransformAsync_WithEntitlement_CustomRoleClaimTypeApplies()
    {
        // Enterprise gets what it configured: `groups` values are roles and reach AdminRoles.
        // The admin assertion also pins a latent defect this change settles — `admin` is
        // written as a ClaimTypes.Role claim, which an identity keyed on `groups` could never
        // resolve, so a custom-role-claim deployment used to get no admin at all.
        var transformation = CreateRoleClaimTypeTransformation(HonuaEdition.Enterprise);

        var result = await transformation.TransformAsync(CreateGroupsPrincipal());

        Assert.True(result.IsInRole("platform-admins"));
        Assert.True(result.IsInRole("admin"));
    }

    [UnitTest]
    public async Task TransformAsync_WithEntitlement_MarksRolesAsClaimsMappingDerived()
    {
        // Provenance for anything that PERSISTS these roles. The portal token exchange copies
        // the transformed ClaimTypes.Role values into a durable record and the restore path
        // never re-runs this transformation, so without a marker an expired entitlement kept
        // being honoured for the token's whole lifetime (honua-server#2997 review).
        var transformation = CreateRoleClaimTypeTransformation(HonuaEdition.Enterprise);

        var result = await transformation.TransformAsync(CreateGroupsPrincipal());

        Assert.NotNull(result.FindFirst(OidcClaimsTransformation.RolesFromClaimsMappingClaimType));
        Assert.Equal(
            ["user"],
            result.FindAll(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType)
                .Select(static claim => claim.Value));
        Assert.DoesNotContain(
            result.FindAll(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType),
            claim => string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public async Task TransformAsync_WithoutEntitlement_DoesNotMarkRolesAsClaimsMappingDerived()
    {
        // Nothing was granted by claims mapping, so there is nothing for a persisted token to
        // revalidate — the marker must not be stamped where it would only cost a lookup.
        var transformation = CreateRoleClaimTypeTransformation(HonuaEdition.Pro);

        var result = await transformation.TransformAsync(CreateGroupsPrincipal());

        Assert.Null(result.FindFirst(OidcClaimsTransformation.RolesFromClaimsMappingClaimType));
    }

    [UnitTest]
    public async Task TransformAsync_MixedDirectAndMappedRoles_RecordsOnlyDirectFallbackRoles()
    {
        var options = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = ["groups"],
            },
        });
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise))
            .BuildServiceProvider();
        var transformation = new OidcClaimsTransformation(
            options, NullLogger<OidcClaimsTransformation>.Instance, services);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "viewer"), new Claim("groups", "editor")],
            "Bearer"));

        var result = await transformation.TransformAsync(principal);

        Assert.True(result.IsInRole("viewer"));
        Assert.True(result.IsInRole("editor"));
        Assert.NotNull(result.FindFirst(OidcClaimsTransformation.RolesFromClaimsMappingClaimType));
        Assert.Equal(
            ["viewer"],
            result.FindAll(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType)
                .Select(static claim => claim.Value));
    }

    [UnitTest]
    public async Task TransformAsync_ProviderSuppliedProvenanceClaims_AreReplacedWithComputedFallback()
    {
        var options = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = ["groups"],
                CustomMappings = new Dictionary<string, string>
                {
                    ["forged_mapping_marker"] = OidcClaimsTransformation.RolesFromClaimsMappingClaimType,
                    ["forged_fallback_role"] = OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType,
                    ["forged_tenant_marker"] = OidcClaimsTransformation.TenantFromClaimsMappingClaimType,
                },
            },
        });
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise))
            .BuildServiceProvider();
        var transformation = new OidcClaimsTransformation(
            options, NullLogger<OidcClaimsTransformation>.Instance, services);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, "viewer"),
                new Claim("groups", "editor"),
                new Claim(OidcClaimsTransformation.RolesFromClaimsMappingClaimType, "1"),
                new Claim(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType, "admin"),
                new Claim(OidcClaimsTransformation.TenantFromClaimsMappingClaimType, "tenant_id"),
                new Claim("forged_mapping_marker", "1"),
                new Claim("forged_fallback_role", "admin"),
                new Claim("forged_tenant_marker", "tenant_id"),
            ],
            "Bearer"));

        var result = await transformation.TransformAsync(principal);

        Assert.True(result.IsInRole("viewer"));
        Assert.True(result.IsInRole("editor"));
        Assert.NotNull(result.FindFirst(OidcClaimsTransformation.RolesFromClaimsMappingClaimType));
        Assert.Equal(
            ["viewer"],
            result.FindAll(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType)
                .Select(static claim => claim.Value));
        Assert.DoesNotContain(
            result.FindAll(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType),
            claim => string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase));
        Assert.Null(result.FindFirst(OidcClaimsTransformation.TenantFromClaimsMappingClaimType));
    }

    [UnitTest]
    public async Task TransformAsync_DefaultRolesOnly_IsNotMarkedAsClaimsMappingDerived()
    {
        // An Enterprise principal whose roles come from the ungated default claim owes nothing
        // to the entitlement, so its portal token must keep working if the license lapses.
        var options = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            ClaimsMapping = new ClaimsMappingOptions { AdditionalRoleClaimTypes = ["groups"] },
        });

        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise))
            .BuildServiceProvider();

        var transformation = new OidcClaimsTransformation(
            options, NullLogger<OidcClaimsTransformation>.Instance, services);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-123"), new Claim("roles", "editors")],
            "Bearer"));

        var result = await transformation.TransformAsync(principal);

        Assert.True(result.IsInRole("editors"));
        Assert.Null(result.FindFirst(OidcClaimsTransformation.RolesFromClaimsMappingClaimType));
    }

    [UnitTest]
    public async Task TransformAsync_DirectRoleAndSkippedRoleMapping_DoesNotMarkRoleAsMappingDerived()
    {
        // The custom-mapping loop skips a target claim type that is already present. Provenance
        // must make the same decision; otherwise a directly issued role is later stripped when
        // the claims-mapping entitlement expires even though the mapping emitted nothing.
        var options = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            ClaimsMapping = new ClaimsMappingOptions
            {
                CustomMappings = new Dictionary<string, string>
                {
                    ["department"] = ClaimTypes.Role,
                },
            },
        });

        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise))
            .BuildServiceProvider();
        var transformation = new OidcClaimsTransformation(
            options, NullLogger<OidcClaimsTransformation>.Instance, services);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("department", "cartography"), new Claim(ClaimTypes.Role, "viewer")],
            "Bearer"));

        var result = await transformation.TransformAsync(principal);

        Assert.True(result.IsInRole("viewer"));
        Assert.False(result.IsInRole("cartography"));
        Assert.Null(result.FindFirst(OidcClaimsTransformation.RolesFromClaimsMappingClaimType));
    }

    [UnitTest]
    public async Task TransformAsync_DirectTenantIdAndMappedTid_MarksSelectedTenantIndependent()
    {
        var transformation = CreateTenantMappingTransformation("tid");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "tenant-direct"), new Claim("department", "tenant-mapped")],
            "Bearer"));

        var result = await transformation.TransformAsync(principal);

        Assert.Equal("tenant-direct", result.FindFirst("tenant_id")?.Value);
        Assert.Equal("tenant-mapped", result.FindFirst("tid")?.Value);
        Assert.Null(result.FindFirst(OidcClaimsTransformation.TenantFromClaimsMappingClaimType));
    }

    [UnitTest]
    public async Task TransformAsync_DirectTidAndMappedTenantId_MarksSelectedTenantDerived()
    {
        var transformation = CreateTenantMappingTransformation("tenant_id");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", "tenant-direct"), new Claim("department", "tenant-mapped")],
            "Bearer"));

        var result = await transformation.TransformAsync(principal);

        Assert.Equal("tenant-mapped", result.FindFirst("tenant_id")?.Value);
        Assert.Equal(
            "tenant_id",
            result.FindFirst(OidcClaimsTransformation.TenantFromClaimsMappingClaimType)?.Value);
    }

    [UnitTest]
    public async Task TransformAsync_WithClaimsMappingEntitlement_AppliesCustomMappings()
    {
        var transformation = CreateTransformation(HonuaEdition.Enterprise);

        var result = await transformation.TransformAsync(CreatePrincipal());

        Assert.Equal("cartography", result.FindFirst("honua_department")?.Value);
        Assert.True(result.IsInRole("editors"));
    }

    [UnitTest]
    public async Task TransformAsync_WithoutCustomMappingConfigured_DoesNotConsultEntitlement()
    {
        // No custom mappings configured: Community deployments must not pay any entitlement
        // penalty for plain OIDC claims normalization.
        var options = Options.Create(new OidcAuthenticationOptions { DefaultRole = "user" });
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Community))
            .BuildServiceProvider();
        var transformation = new OidcClaimsTransformation(
            options,
            NullLogger<OidcClaimsTransformation>.Instance,
            services);

        var result = await transformation.TransformAsync(CreatePrincipal());

        Assert.Equal("user-123", result.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(result.IsInRole("user"));
    }

    private static OidcClaimsTransformation CreateTenantMappingTransformation(string targetClaimType)
    {
        var options = Options.Create(new OidcAuthenticationOptions
        {
            ClaimsMapping = new ClaimsMappingOptions
            {
                CustomMappings = new Dictionary<string, string>
                {
                    ["department"] = targetClaimType,
                },
            },
        });
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise))
            .BuildServiceProvider();
        return new OidcClaimsTransformation(
            options, NullLogger<OidcClaimsTransformation>.Instance, services);
    }
}
