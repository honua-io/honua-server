// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.RateLimiting.Domain;

public sealed record RateLimitRequestDescriptor
{
    public string? ApiKey { get; init; }

    public string? TenantId { get; init; }

    public string? Plan { get; init; }
}

public static class RateLimitPolicyResolver
{
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
