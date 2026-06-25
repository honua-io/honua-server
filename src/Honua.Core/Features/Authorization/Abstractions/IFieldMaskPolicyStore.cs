// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Durable store for field-level security (column masking) policies (#1940). Policies are
/// managed per-layer through the Admin API and resolved per-request to redact attributes
/// from query results. Mirrors <see cref="IRlsPolicyStore"/> (#502).
/// </summary>
public interface IFieldMaskPolicyStore
{
    /// <summary>
    /// Lists all field-mask policies, newest first.
    /// </summary>
    Task<IReadOnlyList<FieldMaskPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific policy by ID, or null when it does not exist.
    /// </summary>
    Task<FieldMaskPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new field-mask policy.
    /// </summary>
    Task<FieldMaskPolicy> CreatePolicyAsync(FieldMaskPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a policy. Returns false when the policy does not exist.
    /// </summary>
    Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the field-mask policies that apply to a request for the given
    /// <paramref name="service"/>/<paramref name="layer"/> across all of the
    /// principal's <paramref name="roles"/>. Wildcards ("*") on role/service/layer
    /// match any value. The masked attributes from the returned policies are unioned
    /// and dropped from query output.
    /// </summary>
    Task<IReadOnlyList<FieldMaskPolicy>> GetEffectivePoliciesAsync(
        IReadOnlyList<string> roles,
        string service,
        string layer,
        CancellationToken cancellationToken = default);
}
