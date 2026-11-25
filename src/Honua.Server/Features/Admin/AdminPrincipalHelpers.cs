// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static class AdminPrincipalHelpers
{
    public static string? ResolveCreator(HttpContext context)
    {
        var apiKeyId = context.User.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(apiKeyId))
        {
            return $"api-key:{apiKeyId}";
        }

        return context.User.Identity?.Name;
    }

    public static string? ResolveRequestedBy(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.User.Identity?.Name))
        {
            return context.User.Identity!.Name;
        }

        var apiKeyId = context.User.FindFirst("api_key_id")?.Value;
        return string.IsNullOrWhiteSpace(apiKeyId) ? null : $"api-key:{apiKeyId}";
    }
}
