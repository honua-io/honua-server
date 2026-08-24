// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Resolves immutable, scheme-qualified actor identity and the request binding
/// components used by deferred authorization, audit, cache isolation, and MCP.
/// Display names are deliberately never identifiers.
/// </summary>
internal static class CanonicalSecurityActor
{
    private const string ApiKeyIdClaim = "api_key_id";
    private const string AuthTypeClaim = "auth_type";
    private const string IssuerClaim = "iss";
    private const string SubjectClaim = "sub";
    internal const string EffectiveTenantClaim = "honua:effective_tenant";
    internal const string ScopeCeilingClaim = "honua:scope_ceiling";
    internal const string CanonicalActorClaim = "honua:canonical_actor";

    public static CanonicalSecurityActorIdentity? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true } identity)
        {
            return null;
        }

        var scheme = NormalizeScheme(principal.FindFirstValue(AuthTypeClaim) ?? identity.AuthenticationType);
        if (scheme is null)
        {
            return null;
        }

        var apiKeyValue = principal.FindFirstValue(ApiKeyIdClaim);
        if (Guid.TryParse(apiKeyValue, out var apiKeyId))
        {
            return new CanonicalSecurityActorIdentity(
                $"{scheme}:api-key:{apiKeyId:D}", scheme, null, null, apiKeyId.ToString("D"), true);
        }

        var subject = NormalizeValue(principal.FindFirstValue(ClaimTypes.NameIdentifier))
            ?? NormalizeValue(principal.FindFirstValue(SubjectClaim));
        if (subject is not null)
        {
            var issuer = NormalizeValue(principal.FindFirstValue(IssuerClaim));
            return new CanonicalSecurityActorIdentity(
                $"{scheme}:subject:{Encode(issuer ?? "-")}:{Encode(subject)}",
                scheme, subject, issuer, null, true);
        }

        if (string.Equals(scheme, "admin", StringComparison.Ordinal))
        {
            return new CanonicalSecurityActorIdentity("admin:bootstrap", scheme, null, null, null, false);
        }

        return null;
    }

    internal static string BuildBindingKey(
        CanonicalSecurityActorIdentity actor,
        string? effectiveTenant,
        ClaimsPrincipal principal)
    {
        var tenant = NormalizeValue(effectiveTenant) ?? "-";
        var scopeCeiling = ResolveScopeCeiling(principal);
        return $"{actor.ActorId}:tenant:{Encode(tenant)}:scope:{Encode(scopeCeiling)}";
    }

    internal static string ResolveScopeCeiling(ClaimsPrincipal principal)
    {
        var scopes = principal.FindAll("scope")
            .Concat(principal.FindAll("scp"))
            .SelectMany(static claim => (claim.Value ?? string.Empty)
                .Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        return scopes.Length == 0 ? "-" : string.Join(' ', scopes);
    }

    internal static void StampRequestBinding(ClaimsPrincipal principal, string? effectiveTenant)
    {
        var actor = Resolve(principal);
        if (actor is null || principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        Replace(identity, CanonicalActorClaim, actor.ActorId);
        Replace(identity, "honua:auth_scheme", actor.AuthenticationScheme);
        Replace(identity, "honua:issuer", actor.SubjectIssuer);
        Replace(identity, EffectiveTenantClaim, NormalizeValue(effectiveTenant));
        Replace(identity, ScopeCeilingClaim, ResolveScopeCeiling(principal));
    }

    private static void Replace(ClaimsIdentity identity, string type, string? value)
    {
        foreach (var claim in identity.FindAll(type).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            identity.AddClaim(new Claim(type, value));
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
