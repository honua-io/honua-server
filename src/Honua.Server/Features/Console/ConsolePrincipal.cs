// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.Console;

/// <summary>
/// Resolves a stable identifier for the requesting principal across the
/// authentication schemes that reach Console endpoints. OIDC/JWT principals
/// carry <see cref="ClaimTypes.NameIdentifier"/> or <c>sub</c>; admin API-key
/// principals (see <c>ApiKeyAuthenticationHandler</c>) carry only
/// <see cref="ClaimTypes.Name"/> plus the optional <c>api_key_id</c>/<c>api_key_name</c>
/// claims. The fallback chain ensures audit fields and session identity stay
/// non-empty regardless of which scheme authenticated the request.
/// </summary>
internal static class ConsolePrincipal
{
    private const string SubjectClaim = "sub";
    private const string ApiKeyIdClaim = "api_key_id";
    private const string ApiKeyNameClaim = "api_key_name";

    /// <summary>
    /// Returns the most-specific identifier we can derive for the principal,
    /// or <see langword="null"/> when the principal is unauthenticated. The
    /// resolved value is used as the audit actor for create/update/patch and
    /// as the user identifier in the session bootstrap response.
    /// </summary>
    public static string? ResolveActorId(ClaimsPrincipal principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        var candidate = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate;

        candidate = principal.FindFirstValue(SubjectClaim);
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate;

        candidate = principal.FindFirstValue(ApiKeyIdClaim);
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate;

        candidate = principal.FindFirstValue(ApiKeyNameClaim);
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate;

        candidate = principal.Identity.Name;
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate;

        candidate = principal.FindFirstValue(ClaimTypes.Name);
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
