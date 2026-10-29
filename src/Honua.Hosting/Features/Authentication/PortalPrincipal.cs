// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

/// <summary>Resolves the authenticated subject used by Portal group membership.</summary>
internal static class PortalPrincipal
{
    internal static string? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return null;
        }

        if (string.Equals(identity.AuthenticationType, AuthenticationExtensions.ApiKeyScheme, StringComparison.OrdinalIgnoreCase))
        {
            var claim = identity.FindFirst("api_key_id");
            var kind = identity.FindFirst("auth_type");
            var actor = CanonicalSecurityActor.Resolve(principal);
            if (claim is null || !CanonicalSecurityActor.IsFrameworkOwnedClaim(claim)
                || kind is null || !CanonicalSecurityActor.IsFrameworkOwnedClaim(kind)
                || !string.Equals(kind.Value, "admin-api-key", StringComparison.Ordinal)
                || !string.Equals(actor?.AuthenticationScheme, AuthenticationExtensions.ApiKeyScheme, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(actor?.ApiKeyId, out var keyId))
            {
                return null;
            }

            return $"api-key:{keyId:D}";
        }

        var subject = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(subject) || subject.StartsWith("api-key:", StringComparison.OrdinalIgnoreCase)
            ? null
            : subject;
    }
}
