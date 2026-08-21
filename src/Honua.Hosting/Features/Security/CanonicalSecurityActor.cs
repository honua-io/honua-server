// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Security;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Resolves an immutable, scheme-qualified actor identity for deferred authorization,
/// audit, and cache isolation. Display names are deliberately never identifiers.
/// </summary>
internal static class CanonicalSecurityActor
{
    private const string ApiKeyIdClaim = "api_key_id";
    private const string AuthTypeClaim = "auth_type";
    private const string IssuerClaim = "iss";
    private const string IdentityIssuerClaim = "honua_identity_issuer";
    private const string OperatorBearerAuthenticationType = "OperatorBearer";
    private const string SubjectClaim = "sub";

    public static CanonicalSecurityActorIdentity? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true } identity)
        {
            return null;
        }

        var apiKeyValue = principal.FindFirstValue(ApiKeyIdClaim);
        if (Guid.TryParse(apiKeyValue, out var apiKeyId))
        {
            var apiKeyScheme = NormalizeScheme(
                principal.FindFirstValue(AuthTypeClaim) ?? identity.AuthenticationType);
            if (apiKeyScheme is null)
            {
                return null;
            }

            return new CanonicalSecurityActorIdentity(
                $"{apiKeyScheme}:api-key:{apiKeyId:D}",
                apiKeyScheme,
                SubjectId: null,
                SubjectIssuer: null,
                ApiKeyId: apiKeyId.ToString("D"),
                IsDurablyRevalidatable: true);
        }

        var subject = NormalizeValue(principal.FindFirstValue(ClaimTypes.NameIdentifier))
            ?? NormalizeValue(principal.FindFirstValue(SubjectClaim));
        if (subject is not null)
        {
            var scheme = IdentityProtocolProvenance.Resolve(principal);
            if (scheme is null)
            {
                return null;
            }

            var isOperatorBearer = string.Equals(
                identity.AuthenticationType,
                OperatorBearerAuthenticationType,
                StringComparison.OrdinalIgnoreCase);
            var isOidc = string.Equals(scheme, IdentityProtocolProvenance.Oidc, StringComparison.Ordinal);
            var issuer = isOidc
                ? isOperatorBearer
                    ? NormalizeValue(principal.FindFirstValue(IdentityIssuerClaim))
                    : NormalizeValue(principal.FindFirstValue(IssuerClaim))
                : null;
            if (isOidc && issuer is null)
            {
                return null;
            }

            return new CanonicalSecurityActorIdentity(
                $"{scheme}:subject:{Encode(issuer ?? "-")}:{Encode(subject)}",
                scheme,
                subject,
                issuer,
                ApiKeyId: null,
                IsDurablyRevalidatable: true);
        }

        // Bootstrap credentials can make a live approval decision, but cannot safely
        // originate deferred work because there is no durable identity to re-resolve.
        var bootstrapScheme = NormalizeScheme(
            principal.FindFirstValue(AuthTypeClaim) ?? identity.AuthenticationType);
        if (string.Equals(bootstrapScheme, "admin", StringComparison.Ordinal))
        {
            return new CanonicalSecurityActorIdentity(
                "admin:bootstrap",
                "admin",
                SubjectId: null,
                SubjectIssuer: null,
                ApiKeyId: null,
                IsDurablyRevalidatable: false);
        }

        return null;
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);

    internal static bool IsBoundIdentity(
        string? actorId,
        string? scheme,
        string? subjectId,
        string? subjectIssuer,
        string? apiKeyId)
    {
        var normalizedScheme = NormalizeScheme(scheme);
        if (normalizedScheme is null || string.IsNullOrWhiteSpace(actorId))
        {
            return false;
        }

        if (Guid.TryParse(apiKeyId, out var keyId))
        {
            return string.Equals(
                actorId,
                $"{normalizedScheme}:api-key:{keyId:D}",
                StringComparison.Ordinal);
        }

        var subject = NormalizeValue(subjectId);
        if (subject is null)
        {
            return false;
        }

        var issuer = NormalizeValue(subjectIssuer);
        if (!IdentityProtocolProvenance.IsSupported(normalizedScheme)
            || (string.Equals(normalizedScheme, IdentityProtocolProvenance.Oidc, StringComparison.Ordinal)
                && issuer is null)
            || (string.Equals(normalizedScheme, IdentityProtocolProvenance.Saml, StringComparison.Ordinal)
                && issuer is not null))
        {
            return false;
        }

        return string.Equals(
            actorId,
            $"{normalizedScheme}:subject:{Encode(issuer ?? "-")}:{Encode(subject)}",
            StringComparison.Ordinal);
    }

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
