// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IRlsPolicyStore
{
    Task<IReadOnlyList<RlsPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    Task<RlsPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    Task<RlsPolicy> CreatePolicyAsync(RlsPolicy policy, CancellationToken cancellationToken = default);

    Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RlsPolicy>> GetEffectivePoliciesAsync(
        IReadOnlyList<string> roles,
        string service,
        string layer,
        CancellationToken cancellationToken = default);
}
