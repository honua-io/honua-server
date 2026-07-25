// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class OidcEntitlementPolicyTests
{
    [UnitTest]
    public void GetDeniedEntitlement_CommunitySingleProvider_DeniesBaseOidc()
    {
        using var services = CreateServices(HonuaEdition.Community);

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(
            services,
            CreateOptions(providerCount: 1));

        denied.Should().Be(FeatureCatalog.OidcAuthenticationKey);
    }

    [UnitTest]
    public void GetDeniedEntitlement_ProSingleProviderWithDefaultClaims_AllowsAuthentication()
    {
        using var services = CreateServices(HonuaEdition.Pro);

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(
            services,
            CreateOptions(providerCount: 1));

        denied.Should().BeNull();
    }

    [UnitTest]
    public void GetDeniedEntitlement_ProMultipleProviders_DeniesMultiProvider()
    {
        using var services = CreateServices(HonuaEdition.Pro);

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(
            services,
            CreateOptions(providerCount: 2));

        denied.Should().Be(FeatureCatalog.OidcMultiProviderKey);
    }

    [UnitTest]
    public void GetDeniedEntitlement_EnterpriseMultipleProviders_AllowsAuthentication()
    {
        using var services = CreateServices(HonuaEdition.Enterprise);

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(
            services,
            CreateOptions(providerCount: 2));

        denied.Should().BeNull();
    }

    [UnitTest]
    public void GetDeniedEntitlement_ProCustomClaimsMapping_DeniesClaimsMapping()
    {
        using var services = CreateServices(HonuaEdition.Pro);
        var options = CreateOptions(providerCount: 1);
        options.ClaimsMapping.CustomMappings["department"] = "honua_department";

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(services, options);

        denied.Should().Be(FeatureCatalog.OidcClaimsMappingKey);
    }

    [UnitTest]
    public void GetDeniedEntitlement_EnterpriseCustomClaimsMapping_AllowsAuthentication()
    {
        using var services = CreateServices(HonuaEdition.Enterprise);
        var options = CreateOptions(providerCount: 1);
        options.ClaimsMapping.RoleClaimType = "groups";

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(services, options);

        denied.Should().BeNull();
    }

    [UnitTest]
    public void GetDeniedEntitlement_ProDuplicatedStockValuesAndDisabledProviderSettings_AllowsAuthentication()
    {
        using var services = CreateServices(HonuaEdition.Pro);
        var options = CreateOptions(providerCount: 1);
        options.AdminRoles = ["admin", "administrator", "admin", "administrator"];
        options.ClaimsMapping.AdditionalRoleClaimTypes = ["groups", "groups"];
        options.Okta = new OktaProviderOptions
        {
            Enabled = false,
            RequestGroupsClaim = true,
        };
        options.Auth0 = new Auth0ProviderOptions
        {
            Enabled = false,
            RoleClaimNamespace = "https://disabled.example.com/roles",
        };

        var denied = OidcEntitlementPolicy.GetDeniedEntitlement(services, options);

        denied.Should().BeNull();
    }

    [UnitTest]
    public async Task ClaimsTransformation_SamlPrincipal_DoesNotApplyOidcCustomMappings()
    {
        var options = new OidcAuthenticationOptions
        {
            ClaimsMapping = new ClaimsMappingOptions
            {
                CustomMappings =
                {
                    ["department"] = ClaimTypes.Role,
                },
            },
        };
        var transformation = new OidcClaimsTransformation(
            Options.Create(options),
            NullLogger<OidcClaimsTransformation>.Instance);
        var identity = new ClaimsIdentity(
            [
                new Claim("auth_type", "saml"),
                new Claim("department", "admin"),
            ],
            OidcAuthenticationExtensions.AdminSessionScheme);
        var principal = new ClaimsPrincipal(identity);

        await transformation.TransformAsync(principal);

        principal.IsInRole("admin").Should().BeFalse();
        identity.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    private static ServiceProvider CreateServices(HonuaEdition edition)
        => new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition))
            .BuildServiceProvider();

    private static OidcAuthenticationOptions CreateOptions(int providerCount)
    {
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = ["groups"],
            },
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://identity.example.com",
                ClientId = "generic-client",
            },
        };

        if (providerCount > 1)
        {
            options.Google = new GoogleProviderOptions
            {
                Enabled = true,
                ClientId = "google-client",
                ClientSecret = "google-secret",
            };
        }

        return options;
    }
}
