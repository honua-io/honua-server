// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Evaluates the edition entitlements required by the configured OIDC runtime.
/// </summary>
internal static class OidcEntitlementPolicy
{
    private static readonly string[] DefaultAdminRoles = ["admin", "administrator"];
    private static readonly string[] DefaultAdditionalRoleClaimTypes = ["groups"];

    /// <summary>
    /// Returns the first missing entitlement that must prevent an OIDC principal from
    /// being accepted, or <see langword="null"/> when the configured runtime is licensed.
    /// </summary>
    internal static string? GetDeniedEntitlement(
        IServiceProvider services,
        OidcAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return GetDeniedEntitlement(
            services.GetService<ILicenseEntitlementService>(),
            options);
    }

    /// <summary>
    /// Returns the first missing entitlement for the request-time, post-configured
    /// OIDC options registered in dependency injection.
    /// </summary>
    internal static string? GetDeniedEntitlement(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
        return GetDeniedEntitlement(
            services.GetService<ILicenseEntitlementService>(),
            options);
    }

    /// <summary>
    /// Returns the first missing entitlement for a supplied license source.
    /// </summary>
    internal static string? GetDeniedEntitlement(
        ILicenseEntitlementService? entitlements,
        OidcAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (entitlements?.CheckEntitlement(FeatureCatalog.OidcAuthenticationKey).IsActive != true)
        {
            return FeatureCatalog.OidcAuthenticationKey;
        }

        if (CountConfiguredProviders(options) > 1 &&
            !entitlements.CheckEntitlement(FeatureCatalog.OidcMultiProviderKey).IsActive)
        {
            return FeatureCatalog.OidcMultiProviderKey;
        }

        if (UsesCustomClaimsMapping(options) &&
            !entitlements.CheckEntitlement(FeatureCatalog.OidcClaimsMappingKey).IsActive)
        {
            return FeatureCatalog.OidcClaimsMappingKey;
        }

        return null;
    }

    /// <summary>
    /// Endpoint filter that rejects OIDC-backed flows unless their base,
    /// provider-count, and claims-mapping entitlements are active.
    /// </summary>
    internal static ValueTask<object?> RequireEndpointEntitlementAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var context = invocationContext.HttpContext;
        var deniedEntitlement = GetDeniedEntitlement(context.RequestServices);
        if (deniedEntitlement is null)
        {
            return next(invocationContext);
        }

        var failure = LicenseGate.RequireEntitlement(
            context,
            deniedEntitlement,
            "OIDC authentication");
        return failure is null
            ? next(invocationContext)
            : ValueTask.FromResult<object?>(failure);
    }

    internal static string CreateFailureMessage(string deniedEntitlement)
        => $"OIDC authentication is not licensed for entitlement '{deniedEntitlement}'.";

    private static int CountConfiguredProviders(OidcAuthenticationOptions options)
    {
        var count = 0;
        count += options.AzureAd?.IsValid == true ? 1 : 0;
        count += options.Google?.IsValid == true ? 1 : 0;
        count += options.Generic?.IsValid == true ? 1 : 0;
        count += options.Okta?.IsValid == true ? 1 : 0;
        count += options.Auth0?.IsValid == true ? 1 : 0;
        return count;
    }

    private static bool UsesCustomClaimsMapping(OidcAuthenticationOptions options)
    {
        var mapping = options.ClaimsMapping;
        if (!string.Equals(mapping.NameClaimType, "name", StringComparison.Ordinal) ||
            !string.Equals(mapping.RoleClaimType, "roles", StringComparison.Ordinal) ||
            !string.Equals(mapping.EmailClaimType, "email", StringComparison.Ordinal) ||
            !string.Equals(mapping.UserIdClaimType, "sub", StringComparison.Ordinal) ||
            mapping.CustomMappings.Count > 0 ||
            !HasDefaultAdditionalRoleClaimTypes(mapping.AdditionalRoleClaimTypes))
        {
            return true;
        }

        if (!string.Equals(options.DefaultRole, "user", StringComparison.Ordinal) ||
            !HasDefaultValues(options.AdminRoles, DefaultAdminRoles))
        {
            return true;
        }

        return (options.Okta?.IsValid == true && options.Okta.RequestGroupsClaim) ||
            (options.Auth0?.IsValid == true &&
                !string.IsNullOrWhiteSpace(options.Auth0.RoleClaimNamespace));
    }

    private static bool HasDefaultValues(string[] values, string[] defaults)
        => values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(defaults);

    private static bool HasDefaultAdditionalRoleClaimTypes(string[] claimTypes)
        => claimTypes.Length == 0 ||
            HasDefaultValues(claimTypes, DefaultAdditionalRoleClaimTypes);
}
