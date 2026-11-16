// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface ICombinedReadPolicyStore
{
    bool CanResolveWith(IFieldMaskPolicyStore fieldStore);

    Task<EffectiveReadPolicies> GetEffectiveReadPoliciesAsync(
        IFieldMaskPolicyStore fieldStore,
        IReadOnlyList<string> roles,
        IReadOnlyCollection<string> services,
        string layer,
        CancellationToken cancellationToken = default);
}

public readonly record struct EffectiveReadPolicies(
    IReadOnlyList<RlsPolicy> RowPolicies,
    IReadOnlyList<FieldMaskPolicy> FieldPolicies);
