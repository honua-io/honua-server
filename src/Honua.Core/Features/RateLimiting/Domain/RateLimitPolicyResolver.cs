// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.RateLimiting.Domain;

/// <summary>
/// Identifies the subject tiers a request belongs to so the applicable rate-limit policy can be
/// resolved by precedence (issue #2158). Any field may be <see langword="null"/> when the request
/// has no value for that tier (for example anonymous traffic carries no API key).
/// </summary>
public sealed record RateLimitRequestDescriptor
{
    /// <summary>The authenticated API-key identity for the request, if any.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The resolved tenant identifier for the request, if any.</summary>
    public string? TenantId { get; init; }

    /// <summary>The billing plan the request's subject belongs to, if known.</summary>
    public string? Plan { get; init; }
}

/// <summary>
/// Resolves the single applicable <see cref="RateLimitPolicy"/> for a request from the configured
/// policy set, honouring the documented tier precedence (API key &gt; tenant &gt; plan).
/// </summary>
/// <remarks>
/// The resolver is pure and side-effect free so it can be unit tested in isolation and reused by
/// the middleware, the admin status surface, and tests without any infrastructure dependency.
/// </remarks>
public static class RateLimitPolicyResolver
{
    /// <summary>
    /// Returns the most specific enabled policy that matches <paramref name="descriptor"/>, or
    /// <see langword="null"/> when no subject-tier policy applies (the caller should then fall back
    /// to any per-endpoint override or the global default).
    /// </summary>
    /// <param name="policies">The candidate policy set (typically every configured policy).</param>
    /// <param name="descriptor">The tiers the current request belongs to.</param>
    public static RateLimitPolicy? Resolve(
        IReadOnlyList<RateLimitPolicy> policies,
        RateLimitRequestDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(descriptor);

        foreach (var scope in RateLimitScopes.TierPrecedence)
        {
            var subject = scope switch
            {
                RateLimitScopes.ApiKey => descriptor.ApiKey,
                RateLimitScopes.Tenant => descriptor.TenantId,
                RateLimitScopes.Plan => descriptor.Plan,
                _ => null,
            };

            if (string.IsNullOrEmpty(subject))
            {
                continue;
            }

            var match = policies.FirstOrDefault(policy =>
                policy.Enabled
                && string.Equals(policy.Scope, scope, StringComparison.OrdinalIgnoreCase)
                && string.Equals(policy.Key, subject, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
