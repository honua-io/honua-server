// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Durable store for row-level security (RLS) policies (#502). Policies are managed
/// per-layer through the Admin API and resolved per-request to constrain query results.
/// </summary>
public interface IRlsPolicyStore
{
    /// <summary>
    /// Lists all RLS policies, newest first.
    /// </summary>
    Task<IReadOnlyList<RlsPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific policy by ID, or null when it does not exist.
    /// </summary>
    Task<RlsPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new RLS policy.
    /// </summary>
    Task<RlsPolicy> CreatePolicyAsync(RlsPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a policy. Returns false when the policy does not exist.
    /// </summary>
    Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the RLS policies that apply to a request for the given
    /// <paramref name="service"/>/<paramref name="layer"/> across all of the
    /// principal's <paramref name="roles"/>. Wildcards ("*") on role/service/layer
    /// match any value. The returned policies are AND-ed together at query time.
    /// </summary>
    Task<IReadOnlyList<RlsPolicy>> GetEffectivePoliciesAsync(
        IReadOnlyList<string> roles,
        string service,
        string layer,
        CancellationToken cancellationToken = default);
}
