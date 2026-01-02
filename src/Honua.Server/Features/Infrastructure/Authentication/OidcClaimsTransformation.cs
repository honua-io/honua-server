// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Claims transformation service for OIDC-authenticated users.
/// Normalizes claims from different providers and adds application-specific claims.
/// </summary>
internal sealed class OidcClaimsTransformation(
    IOptions<OidcAuthenticationOptions> oidcOptions,
    ILogger<OidcClaimsTransformation> logger) : IClaimsTransformation
{
    private readonly OidcAuthenticationOptions _options = oidcOptions.Value;

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

        // Skip transformation for API key authenticated users
        var authType = identity.FindFirst("auth_type")?.Value;
        if (authType is "admin" or "dev-bypass")
        {
            return Task.FromResult(principal);
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
        var roles = GetRoleClaims(identity);
        foreach (var role in roles)
        {
            if (!identity.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == role))
            {
                transformedClaims.Add(new Claim(ClaimTypes.Role, role));
            }
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

        // Add auth_type claim if not present
        if (!identity.HasClaim(c => c.Type == "auth_type"))
        {
            var scheme = identity.AuthenticationType ?? "oidc";
            transformedClaims.Add(new Claim("auth_type", scheme));
        }

        // Apply custom mappings
        foreach (var mapping in _options.ClaimsMapping.CustomMappings)
        {
            var sourceValue = identity.FindFirst(mapping.Key)?.Value;
            if (!string.IsNullOrEmpty(sourceValue) && !identity.HasClaim(c => c.Type == mapping.Value))
            {
                transformedClaims.Add(new Claim(mapping.Value, sourceValue));
            }
        }

        // Add transformed claims to identity
        if (transformedClaims.Count > 0)
        {
            identity.AddClaims(transformedClaims);
            OidcAuthenticationLog.ClaimsTransformed(logger, transformedClaims.Count, userId ?? "unknown");
        }

        return Task.FromResult(principal);
    }

    private static string? FindClaimValue(ClaimsIdentity identity, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var claim = identity.FindFirst(claimType);
            if (claim != null && !string.IsNullOrEmpty(claim.Value))
            {
                return claim.Value;
            }
        }

        return null;
    }

    private List<string> GetRoleClaims(ClaimsIdentity identity)
    {
        var roles = new List<string>();

        // Check standard role claims
        roles.AddRange(identity.FindAll(ClaimTypes.Role).Select(c => c.Value));
        roles.AddRange(identity.FindAll(_options.ClaimsMapping.RoleClaimType).Select(c => c.Value));

        // Check Azure AD specific role claims
        roles.AddRange(identity.FindAll("roles").Select(c => c.Value));

        // Check Google groups claim (if configured)
        roles.AddRange(identity.FindAll("groups").Select(c => c.Value));

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
