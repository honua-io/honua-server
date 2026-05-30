// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.PackageReview.Domain;
using Microsoft.AspNetCore.Http;

namespace Honua.PackageReview;

/// <summary>
/// Builds a <see cref="PackageReviewContext"/> from the calling
/// <see cref="HttpContext"/>'s claims. Lives in Honua.Hosting so both the
/// PackageReview HTTP endpoints (Honua.Server) and the MCP package-review tool
/// (the AI surface) can construct the review context without coupling the
/// latter to the former's endpoint class.
/// </summary>
public static class PackageReviewContextFactory
{
    /// <summary>
    /// Projects the authenticated principal on <paramref name="context"/> into
    /// a <see cref="PackageReviewContext"/> (actor / tenant / subject + the
    /// normalized scope and role sets).
    /// </summary>
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
