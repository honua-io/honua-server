// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory rate limit policy store for the admin API surface.
/// Will be replaced by a persistent implementation when #355 lands.
/// </summary>
internal sealed class InMemoryRateLimitPolicyStore : IRateLimitPolicyStore
{
    private readonly ConcurrentDictionary<Guid, RateLimitPolicy> _policies = new();

    public Task<IReadOnlyList<RateLimitPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RateLimitPolicy> result = _policies.Values.ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<RateLimitPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
    {
        _policies.TryGetValue(policyId, out var policy);
        return Task.FromResult(policy);
    }

    public Task<RateLimitPolicy> CreatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default)
    {
        if (!_policies.TryAdd(policy.PolicyId, policy))
        {
            throw new InvalidOperationException($"Policy with ID '{policy.PolicyId}' already exists.");
        }

        return Task.FromResult(policy);
    }

    public Task<RateLimitPolicy?> UpdatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default)
    {
        if (!_policies.ContainsKey(policy.PolicyId))
        {
            return Task.FromResult<RateLimitPolicy?>(null);
        }

        _policies[policy.PolicyId] = policy;
        return Task.FromResult<RateLimitPolicy?>(policy);
    }

    public Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
        => Task.FromResult(_policies.TryRemove(policyId, out _));

    public Task<RateLimitStatus?> GetStatusAsync(string key, CancellationToken cancellationToken = default)
    {
        var policy = _policies.Values.FirstOrDefault(p =>
            p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Enabled);

        if (policy == null)
        {
            return Task.FromResult<RateLimitStatus?>(null);
        }

        // Placeholder: a real implementation would check actual request counters.
        var status = new RateLimitStatus
        {
            PolicyId = policy.PolicyId,
            Key = key,
            RequestsConsumed = 0,
            RequestsRemaining = policy.RequestsPerWindow,
            WindowResetsAt = DateTimeOffset.UtcNow.Add(policy.WindowDuration),
        };

        return Task.FromResult<RateLimitStatus?>(status);
    }
}
