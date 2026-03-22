// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.RateLimiting.Domain;

namespace Honua.Core.Features.RateLimiting.Abstractions;

/// <summary>
/// Store for rate limit policy definitions and status queries.
/// </summary>
public interface IRateLimitPolicyStore
{
    /// <summary>
    /// Lists all rate limit policies.
    /// </summary>
    Task<IReadOnlyList<RateLimitPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific policy by ID.
    /// </summary>
    Task<RateLimitPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new rate limit policy.
    /// </summary>
    Task<RateLimitPolicy> CreatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing rate limit policy.
    /// </summary>
    Task<RateLimitPolicy?> UpdatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a rate limit policy.
    /// </summary>
    Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current rate limit status for a specific key.
    /// </summary>
    Task<RateLimitStatus?> GetStatusAsync(string key, CancellationToken cancellationToken = default);
}
