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
}
