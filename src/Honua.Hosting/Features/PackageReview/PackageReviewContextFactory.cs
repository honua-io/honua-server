// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.PackageReview.Domain;
using Microsoft.AspNetCore.Http;

namespace Honua.PackageReview;

public static class PackageReviewContextFactory
{
    public static PackageReviewContext FromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scopes = context.User.Claims
            .Where(static claim => string.Equals(claim.Type, "scope", StringComparison.Ordinal) ||
                                   string.Equals(claim.Type, "scp", StringComparison.Ordinal) ||
                                   string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal))
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var roles = context.User.Claims
            .Where(static claim => string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal) ||
                                   string.Equals(claim.Type, "roles", StringComparison.Ordinal) ||
                                   string.Equals(claim.Type, "role", StringComparison.Ordinal))
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new PackageReviewContext
        {
            ActorId = context.User.Identity?.Name,
            TenantId = context.User.FindFirst("tenant_id")?.Value,
            SubjectId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                        context.User.FindFirst("sub")?.Value,
            Scopes = scopes,
            Roles = roles
        };
    }
}
