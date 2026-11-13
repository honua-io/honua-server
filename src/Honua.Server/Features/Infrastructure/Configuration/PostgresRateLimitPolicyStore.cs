// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed class PostgresRateLimitPolicyStore(PostgresConfigurationDocuments documents) : IRateLimitPolicyStore
{
    private readonly ConfigurationCollectionStore<RateLimitPolicy> _store = new(documents,
        new("rate-limit-policy", SharedConfigurationJsonContext.Default.RateLimitPolicy));

    public async Task<IReadOnlyList<RateLimitPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default)
        => (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).Documents.Select(document => document.Value).ToArray();

    public async Task<RateLimitPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
        => (await ListPoliciesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(policy => policy.PolicyId == policyId);

    public Task<RateLimitPolicy> CreatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default)
        => _store.CreateAsync(policy.PolicyId.ToString("D"), policy, cancellationToken);

    public Task<RateLimitPolicy?> UpdatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default)
        => _store.MutateAsync(policy.PolicyId.ToString("D"), current => current is null ? null : policy, cancellationToken);

    public Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
        => _store.DeleteAsync(policyId.ToString("D"), cancellationToken);

    public async Task<RateLimitStatus?> GetStatusAsync(string key, CancellationToken cancellationToken = default)
    {
        var policy = (await ListPoliciesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Enabled && candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return policy is null ? null : new RateLimitStatus
        {
            PolicyId = policy.PolicyId, Key = key, RequestsConsumed = 0,
            RequestsRemaining = policy.RequestsPerWindow, WindowResetsAt = DateTimeOffset.UtcNow.Add(policy.WindowDuration)
        };
    }
}
