// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Claims transformation service for OIDC-authenticated users.
/// Normalizes claims from different providers and adds application-specific claims.
/// Custom claims mapping (<c>ClaimsMapping:CustomMappings</c> /
/// <c>ClaimsMapping:AdditionalRoleClaimTypes</c>) is an Enterprise entitlement
/// (identity.claims-mapping, #2997): when it is not active, those configured mappings are
/// skipped (soft-degrade to default claims normalization) rather than failing authentication —
/// the token-validation pipeline itself is never gated by edition.
/// </summary>
internal sealed class OidcClaimsTransformation(
    IOptions<OidcAuthenticationOptions> oidcOptions,
    ILogger<OidcClaimsTransformation> logger,
    IServiceProvider serviceProvider) : IClaimsTransformation
{
    private readonly OidcAuthenticationOptions _options = oidcOptions.Value;

    /// <summary>
    /// Marker claim recording that this principal's roles were produced with the Enterprise
    /// <c>identity.claims-mapping</c> entitlement active. Surfaces to anything that PERSISTS
    /// the transformed roles — the ArcGIS portal token exchange — so the restore path can
    /// revalidate them against the live entitlement rather than trusting a snapshot taken while
    /// the license was valid (honua-server#2997 review).
    /// </summary>
    internal const string RolesFromClaimsMappingClaimType = "honua_roles_from_claims_mapping";

    /// <summary>
    /// Repeated marker claim carrying each role that remains valid when
    /// <c>identity.claims-mapping</c> is inactive. Emitted only when the full role set depends
    /// on claims mapping, so portal-token persistence can soft-degrade mixed direct/mapped role
    /// sets instead of dropping every role together.
    /// </summary>
    internal const string RolesWithoutClaimsMappingClaimType = "honua_roles_without_claims_mapping";

    /// <summary>
    /// Provenance marker for a TENANT claim synthesized by <c>ClaimsMapping:CustomMappings</c>.
    /// Persisted alongside the roles marker so the portal-token restore can revalidate a
    /// mapping-derived tenant against the live entitlement (honua-server#2997 review).
    /// </summary>
    internal const string TenantFromClaimsMappingClaimType = "honua_tenant_from_claims_mapping";

    /// <summary>
    /// Claim types a custom mapping can populate to decide the tenant scope: the canonical
    /// <c>tenant_id</c> and the Azure <c>tid</c> that the portal credential verifier falls
    /// back to. Kept in sync with <c>OidcPortalCredentialVerifier</c>.
    /// </summary>
    private static readonly string[] TenantClaimTypes = ["tenant_id", "tid"];

    private static readonly HashSet<string> ReservedProvenanceClaimTypes =
    [
        RolesFromClaimsMappingClaimType,
        RolesWithoutClaimsMappingClaimType,
        TenantFromClaimsMappingClaimType,
    ];

    /// <summary>
    /// Transforms claims from OIDC providers to normalized application claims.
    /// </summary>
    /// <param name="principal">The claims principal from authentication.</param>
    /// <returns>The transformed claims principal.</returns>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        // These markers are framework-owned authorization provenance, not issuer claims. An
        // OIDC provider must not be able to choose the fallback roles restored after the live
        // claims-mapping entitlement expires. Remove every externally supplied copy (including
        // copies on secondary identities), then recompute the exact markers below. Re-running
        // this transformation is safe because previously computed markers are recomputed too.
        RemoveReservedProvenanceClaims(principal);

        // Skip transformation for API key authenticated users (including
        // layer-scoped write keys, #1637, which must not be granted a default
        // role that would widen their tightly scoped write authority).
        var authType = identity.FindFirst("auth_type")?.Value;
        if (authType is "admin" or "dev-bypass" or LayerScopedWriteKey.AuthType)
        {
            return Task.FromResult(principal);
        }

        // #2997: custom claims mapping is Enterprise (identity.claims-mapping). When configured
        // but unentitled, skip the custom mappings and additional role claim types while default
        // claims normalization continues to run.
        // A non-default PRIMARY RoleClaimType is a custom mapping too. Without counting it,
        // an unentitled deployment could point RoleClaimType at `groups` and have raw provider
        // group values read as roles and match AdminRoles, straight past this gate
        // (honua-server#2997 review).
        var customRoleClaimType = !string.Equals(
            _options.ClaimsMapping.RoleClaimType,
            ClaimsMappingOptions.DefaultRoleClaimType,
            StringComparison.OrdinalIgnoreCase);
        var customMappingConfigured = _options.ClaimsMapping.CustomMappings.Count > 0
            || _options.ClaimsMapping.AdditionalRoleClaimTypes.Length > 0
            || customRoleClaimType;
        var claimsMappingEntitled = !customMappingConfigured
            || LicenseGate.IsEntitlementActive(serviceProvider, FeatureCatalog.OidcClaimsMappingKey);
        if (customMappingConfigured && !claimsMappingEntitled)
        {
            OidcAuthenticationLog.CustomClaimsMappingNotEntitled(logger);
        }

        var transformedClaims = new List<Claim>();

        // Normalize user ID claim
        var userId = FindClaimValue(identity,
            _options.ClaimsMapping.UserIdClaimType,
            ClaimTypes.NameIdentifier,
            "sub",
            "oid");

        if (!string.IsNullOrEmpty(userId) && !identity.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
        {
            transformedClaims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }

        // Normalize name claim
        var name = FindClaimValue(identity,
            _options.ClaimsMapping.NameClaimType,
            ClaimTypes.Name,
            "name",
            "preferred_username",
            "given_name");

        if (!string.IsNullOrEmpty(name) && !identity.HasClaim(c => c.Type == ClaimTypes.Name))
        {
            transformedClaims.Add(new Claim(ClaimTypes.Name, name));
        }

        // Normalize email claim
        var email = FindClaimValue(identity,
            _options.ClaimsMapping.EmailClaimType,
            ClaimTypes.Email,
            "email",
            "upn");

        if (!string.IsNullOrEmpty(email) && !identity.HasClaim(c => c.Type == ClaimTypes.Email))
        {
            transformedClaims.Add(new Claim(ClaimTypes.Email, email));
        }

        // Map roles from provider-specific claims
        var rolesWithoutMapping = GetRoleClaims(identity, claimsMappingEntitled: false);
        var roles = GetRoleClaims(identity, claimsMappingEntitled);
        var fallbackRoles = BuildEffectiveRoles(identity, rolesWithoutMapping);
        var fullRoles = BuildEffectiveRoles(identity, roles);

        // A CustomMappings entry targeting ClaimTypes.Role runs after the gathered-role and
        // admin passes below. Include any value it will actually emit in the full persisted set
        // without changing the existing transformation order.
        if (claimsMappingEntitled && !identity.HasClaim(claim => claim.Type == ClaimTypes.Role))
        {
            foreach (var mapping in _options.ClaimsMapping.CustomMappings.Where(
                         static mapping => string.Equals(
                             mapping.Value, ClaimTypes.Role, StringComparison.Ordinal)))
            {
                var sourceValue = identity.FindFirst(mapping.Key)?.Value;
                if (!string.IsNullOrEmpty(sourceValue) &&
                    !fullRoles.Contains(sourceValue, StringComparer.OrdinalIgnoreCase))
                {
                    fullRoles.Add(sourceValue);
                }
            }
        }

        // Provenance for anything that PERSISTS these roles. A portal token exchange copies the
        // transformed ClaimTypes.Role values into a durable record, and restoring them later
        // re-admits roles the live gate would now refuse — including a synthesized `admin` —
        // because the restore path never re-runs this transformation. Marking the principal
        // lets the exchange record that its roles depend on the entitlement, so the restore can
        // revalidate instead of trusting them forever (honua-server#2997 review).
        // A CustomMappings entry can target ClaimTypes.Role directly, which emits a role LATER
        // in this method — after GetRoleClaims has already run — so counting gathered roles
        // alone missed it and the principal went unmarked. The portal exchange then persisted
        // that custom role as entitlement-independent and it kept authorizing requests after
        // identity.claims-mapping expired (honua-server#2997 review).
        // The absence check MUST mirror the mapping loop below, which skips a mapping whose
        // target claim type is already present. Without it, an identity that already carries a
        // directly-issued role plus a configured `x -> ClaimTypes.Role` mapping was marked
        // mapping-derived even though the loop emitted nothing - so an expired entitlement
        // stripped a legitimate role that direct OIDC authentication keeps
        // (honua-server#2997 review).
        var rolesDependOnClaimsMapping = claimsMappingEntitled
            && !new HashSet<string>(fullRoles, StringComparer.OrdinalIgnoreCase)
                .SetEquals(fallbackRoles);

        // Roles are not the only authorization claim a CustomMappings entry can synthesize.
        // A mapping may target `tenant_id` (or the Azure `tid` the verifier falls back to),
        // which the portal exchange persists and TenantContextMiddleware later uses to select
        // the tenant scope. Marking only role provenance meant an expired entitlement dropped
        // the mapping-derived roles while the mapping-derived TENANT kept authorizing
        // cross-tenant access indefinitely (honua-server#2997 review).
        var mappedTenantClaimType = ResolveMappedTenantClaimType(identity, claimsMappingEntitled);
        foreach (var role in roles.Where(role => !identity.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == role)))
        {
            transformedClaims.Add(new Claim(ClaimTypes.Role, role));
        }

        // Add default role if no roles found
        if (roles.Count == 0 && !identity.HasClaim(c => c.Type == ClaimTypes.Role))
        {
            transformedClaims.Add(new Claim(ClaimTypes.Role, _options.DefaultRole));
        }

        // Check if user should have admin role based on provider roles
        var hasAdminRole = _options.AdminRoles.Any(adminRole =>
            roles.Contains(adminRole, StringComparer.OrdinalIgnoreCase));

        if (hasAdminRole && !identity.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin"))
        {
            transformedClaims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        if (mappedTenantClaimType is not null &&
            !identity.HasClaim(c => c.Type == TenantFromClaimsMappingClaimType))
        {
            transformedClaims.Add(new Claim(TenantFromClaimsMappingClaimType, mappedTenantClaimType));
        }

        if (rolesDependOnClaimsMapping && !identity.HasClaim(c => c.Type == RolesFromClaimsMappingClaimType))
        {
            transformedClaims.Add(new Claim(RolesFromClaimsMappingClaimType, "1"));
            transformedClaims.AddRange(fallbackRoles
                .Where(role => !identity.HasClaim(
                    claim => claim.Type == RolesWithoutClaimsMappingClaimType &&
                        string.Equals(claim.Value, role, StringComparison.OrdinalIgnoreCase)))
                .Select(role => new Claim(RolesWithoutClaimsMappingClaimType, role)));
        }

        // Add auth_type claim if not present
        if (!identity.HasClaim(c => c.Type == "auth_type"))
        {
            var scheme = identity.AuthenticationType ?? "oidc";
            transformedClaims.Add(new Claim("auth_type", scheme));
        }

        // Apply custom mappings (Enterprise identity.claims-mapping only, #2997)
        if (claimsMappingEntitled)
        {
            foreach (var mapping in _options.ClaimsMapping.CustomMappings)
            {
                var sourceValue = identity.FindFirst(mapping.Key)?.Value;
                if (!string.IsNullOrEmpty(sourceValue) && !identity.HasClaim(c => c.Type == mapping.Value))
                {
                    transformedClaims.Add(new Claim(mapping.Value, sourceValue));
                }
            }
        }

        // Add transformed claims to identity
        if (transformedClaims.Count > 0)
        {
            identity.AddClaims(transformedClaims);
            OidcAuthenticationLog.ClaimsTransformed(logger, transformedClaims.Count);
        }

        // Gating the claim GATHERING above is not sufficient on its own. The JWT/OIDC handlers
        // install ClaimsMapping.RoleClaimType as TokenValidationParameters.RoleClaimType, so the
        // identity itself resolves IsInRole / [Authorize(Roles=...)] against THAT claim type,
        // never consulting the ClaimTypes.Role claims this transformation just normalized into.
        // Re-home the identity so role resolution reads the normalized claims. This settles two
        // things at once:
        //   * unentitled: raw `groups` values can no longer satisfy a role check by bypassing
        //     the gate above (honua-server#2997 review); and
        //   * entitled: the `admin` role synthesized from AdminRoles becomes visible at all —
        //     it is written as ClaimTypes.Role, which an identity keyed on `groups` could never
        //     resolve, so a custom-role-claim deployment previously got no admin.
        // Doing it here rather than at handler construction keeps the gate LIVE: applying or
        // expiring a license takes effect on the next request instead of at the next restart,
        // matching the other #2997/#2998 gates.
        if (customRoleClaimType &&
            !string.Equals(identity.RoleClaimType, ClaimTypes.Role, StringComparison.Ordinal))
        {
            return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
                identity.Claims,
                identity.AuthenticationType,
                identity.NameClaimType,
                ClaimTypes.Role)));
        }

        return Task.FromResult(principal);
    }

    private static string? FindClaimValue(ClaimsIdentity identity, params string[] claimTypes)
    {
        return claimTypes
            .Select(identity.FindFirst)
            .FirstOrDefault(claim => claim != null && !string.IsNullOrEmpty(claim.Value))
            ?.Value;
    }

    private static void RemoveReservedProvenanceClaims(ClaimsPrincipal principal)
    {
        foreach (var claimsIdentity in principal.Identities)
        {
            foreach (var claim in claimsIdentity.Claims
                         .Where(claim => ReservedProvenanceClaimTypes.Contains(claim.Type))
                         .ToArray())
            {
                claimsIdentity.TryRemoveClaim(claim);
            }
        }
    }

    private List<string> GetRoleClaims(ClaimsIdentity identity, bool claimsMappingEntitled)
    {
        var roles = new List<string>();

        // Check standard role claims
        roles.AddRange(identity.FindAll(ClaimTypes.Role).Select(c => c.Value));

        // Check Azure AD specific role claims (hardcoded for backward compatibility). This is
        // the ungated default and stays available to every edition.
        roles.AddRange(identity.FindAll(ClaimsMappingOptions.DefaultRoleClaimType).Select(c => c.Value));

        // A configured PRIMARY role claim type is only honoured when entitled, for the same
        // reason as the additional types below (honua-server#2997 review).
        if (claimsMappingEntitled)
        {
            roles.AddRange(identity.FindAll(_options.ClaimsMapping.RoleClaimType).Select(c => c.Value));
        }

        // Check provider-configurable additional role claim types
        // (e.g. Okta "groups", Auth0 "{namespace}/roles", "{namespace}/permissions") —
        // Enterprise identity.claims-mapping only (#2997).
        if (claimsMappingEntitled)
        {
            foreach (var claimType in _options.ClaimsMapping.AdditionalRoleClaimTypes)
            {
                roles.AddRange(identity.FindAll(claimType).Select(c => c.Value));
            }
        }

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> BuildEffectiveRoles(ClaimsIdentity identity, List<string> gatheredRoles)
    {
        var effectiveRoles = new List<string>(gatheredRoles);
        if (effectiveRoles.Count == 0 && !identity.HasClaim(claim => claim.Type == ClaimTypes.Role))
        {
            effectiveRoles.Add(_options.DefaultRole);
        }

        var hasAdminRole = _options.AdminRoles.Any(adminRole =>
            effectiveRoles.Contains(adminRole, StringComparer.OrdinalIgnoreCase));
        if (hasAdminRole &&
            !identity.HasClaim(claim =>
                claim.Type == ClaimTypes.Role &&
                string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)) &&
            !effectiveRoles.Contains("admin", StringComparer.OrdinalIgnoreCase))
        {
            effectiveRoles.Add("admin");
        }

        return effectiveRoles;
    }

    private string? ResolveMappedTenantClaimType(
        ClaimsIdentity identity,
        bool claimsMappingEntitled)
    {
        if (!claimsMappingEntitled)
        {
            return null;
        }

        // The verifier selects tenant_id before tid. Stop at the first claim type that will be
        // present after this transformation and report provenance only when that winning claim
        // is emitted by a custom mapping. A mapped fallback must not taint a higher-precedence
        // tenant issued directly by the provider.
        foreach (var claimType in TenantClaimTypes)
        {
            if (identity.HasClaim(claim => claim.Type == claimType))
            {
                return null;
            }

            var mappingEmitsClaim = _options.ClaimsMapping.CustomMappings.Any(mapping =>
                string.Equals(mapping.Value, claimType, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(identity.FindFirst(mapping.Key)?.Value));
            if (mappingEmitsClaim)
            {
                return claimType;
            }
        }

        return null;
    }
}
