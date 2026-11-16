// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

internal enum ActorResolution
{
    Name,
    SubjectOrName,
    AdminName,
    ApiKeyOrName,
}

internal static class ActorResolver
{
    // Each policy retains the endpoint's identity precedence and empty-value handling.
    public static string Resolve(HttpContext context, ActorResolution resolution)
    {
        var principal = context.User;
        return resolution switch
        {
            ActorResolution.Name => principal?.Identity?.Name ?? AuditEvent.AnonymousActor,
            ActorResolution.SubjectOrName => principal!.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal?.Identity?.Name ?? AuditEvent.AnonymousActor,
            ActorResolution.AdminName => principal?.Identity?.Name is { Length: > 0 } name ? name : "admin",
            ActorResolution.ApiKeyOrName => principal!.FindFirst("api_key_id")?.Value is { } apiKeyId
                && !string.IsNullOrWhiteSpace(apiKeyId)
                    ? $"api-key:{apiKeyId}"
                    : principal?.Identity?.Name ?? AuditEvent.AnonymousActor,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };
    }
}
