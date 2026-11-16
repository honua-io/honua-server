// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IFieldMaskPolicyStore
{
    Task<IReadOnlyList<FieldMaskPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    Task<FieldMaskPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    Task<FieldMaskPolicy> CreatePolicyAsync(FieldMaskPolicy policy, CancellationToken cancellationToken = default);

    Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldMaskPolicy>> GetEffectivePoliciesAsync(
        IReadOnlyList<string> roles,
        string service,
        string layer,
        CancellationToken cancellationToken = default);
}
