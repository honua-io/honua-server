// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Resolves immutable, scheme-qualified actor identity and the request binding
/// components used by deferred authorization, audit, cache isolation, and MCP.
/// Display names are deliberately never identifiers.
/// </summary>
internal static class CanonicalSecurityActor
{
    private const string ApiKeyIdClaim = "api_key_id";
    private const string IssuerClaim = "iss";
    private const string SubjectClaim = "sub";
    internal const string EffectiveTenantClaim = "honua:effective_tenant";
    internal const string ScopeCeilingClaim = "honua:scope_ceiling";
    internal const string CanonicalActorClaim = "honua:canonical_actor";
    internal const string AuthenticationSchemeClaim = "honua:auth_scheme";
    internal const string FrameworkOwnedClaimProperty = "honua:framework_owned";

    public static CanonicalSecurityActorIdentity? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return null;
        }

        var scheme = NormalizeScheme(FindStampedValue(principal, AuthenticationSchemeClaim))
            ?? NormalizeScheme(identity.AuthenticationType);
        if (scheme is null)
        {
            return null;
        }

        var apiKeyValue = identity.FindFirst(ApiKeyIdClaim)?.Value;
        if (string.Equals(scheme, AuthenticationExtensions.ApiKeyScheme, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(apiKeyValue, out var apiKeyId))
        {
            return new CanonicalSecurityActorIdentity(
                $"{scheme}:api-key:{apiKeyId:D}", scheme, null, null, apiKeyId.ToString("D"), true);
        }

        var subject = NormalizeValue(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            ?? NormalizeValue(identity.FindFirst(SubjectClaim)?.Value);
        if (subject is not null)
        {
            var issuer = NormalizeValue(identity.FindFirst(IssuerClaim)?.Value);
            return new CanonicalSecurityActorIdentity(
                $"{scheme}:subject:{Encode(issuer ?? "-")}:{Encode(subject)}",
                scheme, subject, issuer, null, true);
        }

        if (string.Equals(scheme, "admin", StringComparison.Ordinal))
        {
            return new CanonicalSecurityActorIdentity("admin:bootstrap", scheme, null, null, null, false);
        }

        var name = NormalizeValue(identity.Name);
        return name is null
            ? null
            : new CanonicalSecurityActorIdentity(
                $"{scheme}:name:{Encode(name)}", scheme, null, null, null, false);
    }

    internal static string BuildBindingKey(
        CanonicalSecurityActorIdentity actor,
        string? effectiveTenant,
        ClaimsPrincipal principal,
        string? credentialFingerprint)
    {
        var normalizedTenant = NormalizeValue(effectiveTenant);
        var tenant = normalizedTenant is null ? "none" : $"value:{normalizedTenant}";
        var scopeCeiling = ResolveScopeCeiling(principal);
        var credential = NormalizeValue(credentialFingerprint) ?? "not-bearer";
        return $"{actor.ActorId}:tenant:{Encode(tenant)}:scope:{Encode(scopeCeiling)}:credential:{Encode(credential)}";
    }

    internal static string ResolveScopeCeiling(ClaimsPrincipal principal)
    {
        if (!OperatorScopeCatalog.IsScopeGoverned(principal))
        {
            return "not-governed";
        }

        var scopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        return scopes.Length == 0 ? "governed:none" : $"governed:set:{string.Join(' ', scopes)}";
    }

    internal static void StampRequestBinding(ClaimsPrincipal principal, string? effectiveTenant)
    {
        var actor = Resolve(principal);
        if (actor is null || principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        Replace(identity, CanonicalActorClaim, actor.ActorId);
        Replace(identity, AuthenticationSchemeClaim, actor.AuthenticationScheme);
        Replace(identity, "honua:issuer", actor.SubjectIssuer);
        Replace(identity, EffectiveTenantClaim, NormalizeValue(effectiveTenant));
        Replace(identity, ScopeCeilingClaim, ResolveScopeCeiling(principal));
    }

    /// <summary>
    /// Reads a request-binding value only when it carries in-memory framework
    /// provenance that cannot be supplied by an OIDC token payload.
    /// </summary>
    internal static string? FindStampedValue(ClaimsPrincipal principal, string claimType) =>
        principal.FindAll(claimType)
            .FirstOrDefault(IsFrameworkOwnedClaim)?.Value;

    internal static bool IsFrameworkOwnedClaim(Claim claim) =>
        claim.Properties.TryGetValue(FrameworkOwnedClaimProperty, out var value)
        && string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase);

    internal static void StampFrameworkClaim(ClaimsIdentity identity, string type, string? value) =>
        Replace(identity, type, value);

    /// <summary>
    /// Returns whether the principal was authenticated by one of the framework-owned
    /// bearer handlers. Issuer-supplied claims are deliberately not consulted.
    /// </summary>
    internal static bool IsBearerPrincipal(ClaimsPrincipal? principal) =>
        principal is not null
        && principal.Identities.Any(static identity => identity.IsAuthenticated)
        && (IsBearerScheme(FindStampedValue(principal, AuthenticationSchemeClaim))
            || principal.Identities.Any(static identity =>
                identity.IsAuthenticated && IsBearerScheme(identity.AuthenticationType)));

    /// <summary>
    /// Returns whether the principal came from the external OIDC JWT bearer
    /// validator. Unlike Honua's operator bearer, these callers are tenant-scoped
    /// and must not reach shared data/control-plane middleware without a validated
    /// tenant claim or authorized override.
    /// </summary>
    internal static bool IsTenantScopedBearerPrincipal(ClaimsPrincipal? principal) =>
        principal is not null
        && principal.Identities.Any(static identity => identity.IsAuthenticated)
        && (string.Equals(
                FindStampedValue(principal, AuthenticationSchemeClaim),
                OidcAuthenticationExtensions.JwtBearerScheme,
                StringComparison.OrdinalIgnoreCase)
            || principal.Identities.Any(static identity =>
                identity.IsAuthenticated
                && string.Equals(
                    identity.AuthenticationType,
                    OidcAuthenticationExtensions.JwtBearerScheme,
                    StringComparison.OrdinalIgnoreCase)));

    private static bool IsBearerScheme(string? scheme) =>
        string.Equals(scheme, OidcAuthenticationExtensions.JwtBearerScheme, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, OidcAuthenticationExtensions.OperatorBearerScheme, StringComparison.OrdinalIgnoreCase);

    private static void Replace(ClaimsIdentity identity, string type, string? value)
    {
        foreach (var claim in identity.FindAll(type).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            var claim = new Claim(type, value);
            claim.Properties[FrameworkOwnedClaimProperty] = bool.TrueString;
            identity.AddClaim(claim);
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static string? NormalizeScheme(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized.ToLowerInvariant();
    }

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal sealed record CanonicalSecurityActorIdentity(
    string ActorId,
    string AuthenticationScheme,
    string? SubjectId,
    string? SubjectIssuer,
    string? ApiKeyId,
    bool IsDurablyRevalidatable);
