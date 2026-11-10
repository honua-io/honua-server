// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.RateLimiting.Domain;

namespace Honua.Core.Features.RateLimiting.Abstractions;

public interface IRateLimitPolicyStore
{
    Task<IReadOnlyList<RateLimitPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    Task<RateLimitPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    Task<RateLimitPolicy> CreatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default);

    Task<RateLimitPolicy?> UpdatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default);

    Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    Task<RateLimitStatus?> GetStatusAsync(string key, CancellationToken cancellationToken = default);
}
