// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory <see cref="IFieldMaskPolicyStore"/> default (#1940). Registered via TryAdd so a
/// durable provider implementation (e.g. <c>PostgresFieldMaskPolicyStore</c>) wins when
/// present; keeps non-Postgres deployments and tests functional without persistence.
/// Mirrors <see cref="InMemoryRlsPolicyStore"/>.
/// </summary>
internal sealed class InMemoryFieldMaskPolicyStore : IFieldMaskPolicyStore
{
    private readonly ConcurrentDictionary<Guid, FieldMaskPolicy> _policies = new();

    public Task<IReadOnlyList<FieldMaskPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FieldMaskPolicy> result = _policies.Values
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<FieldMaskPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
        => Task.FromResult(_policies.TryGetValue(policyId, out var policy) ? policy : null);

    public Task<FieldMaskPolicy> CreatePolicyAsync(FieldMaskPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var duplicate = _policies.Values.Any(existing =>
            string.Equals(existing.Role, policy.Role, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Service, policy.Service, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Layer, policy.Layer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Attribute, policy.Attribute, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException(
                $"A field-mask policy for role '{policy.Role}', service '{policy.Service}', layer '{policy.Layer}', attribute '{policy.Attribute}' already exists.");
        }

        _policies[policy.PolicyId] = policy;
        return Task.FromResult(policy);
    }

    public Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
        => Task.FromResult(_policies.TryRemove(policyId, out _));

    public Task<IReadOnlyList<FieldMaskPolicy>> GetEffectivePoliciesAsync(
        IReadOnlyList<string> roles,
        string service,
        string layer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var loweredRoles = roles
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => r.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<FieldMaskPolicy> matches = _policies.Values
            .Where(p =>
                (p.Service == "*" || string.Equals(p.Service, service, StringComparison.OrdinalIgnoreCase)) &&
                (p.Layer == "*" || string.Equals(p.Layer, layer, StringComparison.OrdinalIgnoreCase)) &&
                (p.Role == "*" || loweredRoles.Contains(p.Role.Trim().ToLowerInvariant())))
            .OrderBy(p => p.CreatedAt)
            .ToList();

        return Task.FromResult(matches);
    }
}
