// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.RateLimiting.Domain;

/// <summary>
/// Well-known <see cref="RateLimitPolicy.Scope"/> values and their tier precedence (issue #2158).
/// </summary>
/// <remarks>
/// Rate-limit policies can be defined for three overlapping subject tiers — a billing
/// <see cref="Plan"/>, a <see cref="Tenant"/>, or an individual <see cref="ApiKey"/> — plus an
/// orthogonal per-<see cref="Endpoint"/> override. When more than one tier matches a request the
/// most specific tier wins (see <see cref="TierPrecedence"/>): an explicit per-API-key quota
/// overrides a per-tenant quota, which overrides the per-plan quota.
/// </remarks>
public static class RateLimitScopes
{
    /// <summary>Per billing-plan scope (least specific subject tier).</summary>
    public const string Plan = "plan";

    /// <summary>Per-tenant scope.</summary>
    public const string Tenant = "tenant";

    /// <summary>Per-API-key scope (most specific subject tier).</summary>
    public const string ApiKey = "api-key";

    /// <summary>Per-endpoint override scope (orthogonal to the subject tiers).</summary>
    public const string Endpoint = "endpoint";

    /// <summary>
    /// Subject-tier precedence, most specific first. The resolver walks this order and returns the
    /// first matching, enabled policy so callers get a single, documented override outcome.
    /// </summary>
    public static IReadOnlyList<string> TierPrecedence { get; } = new[] { ApiKey, Tenant, Plan };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="scope"/> is one of the well-known
    /// scope values (case-insensitive).
    /// </summary>
    /// <param name="scope">The candidate scope value.</param>
    public static bool IsKnown(string? scope) =>
        string.Equals(scope, Plan, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Tenant, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, ApiKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Endpoint, StringComparison.OrdinalIgnoreCase);
}
