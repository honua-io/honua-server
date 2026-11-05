// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.Console;

internal static class ConsolePrincipal
{
    private const string SubjectClaim = "sub";
    private const string ApiKeyIdClaim = "api_key_id";
    private const string ApiKeyNameClaim = "api_key_name";

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
