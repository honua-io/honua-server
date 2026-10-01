// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>Optional compatible-store capability for fresh row and field policy reads.</summary>
public interface ICombinedReadPolicyStore
{
    /// <summary>Checks the actual registered field store, including its provider and schema.</summary>
    bool CanResolveWith(IFieldMaskPolicyStore fieldStore);

    /// <summary>Reads both policy sets for all service scopes without caching evaluated policies.</summary>
    Task<EffectiveReadPolicies> GetEffectiveReadPoliciesAsync(
        IFieldMaskPolicyStore fieldStore,
        IReadOnlyList<string> roles,
        IReadOnlyCollection<string> services,
        string layer,
        CancellationToken cancellationToken = default);
}

/// <summary>Fresh effective policies for one read operation.</summary>
/// <param name="RowPolicies">Row policies de-duplicated by policy id.</param>
/// <param name="FieldPolicies">Field policies de-duplicated by policy id.</param>
public readonly record struct EffectiveReadPolicies(
    IReadOnlyList<RlsPolicy> RowPolicies,
    IReadOnlyList<FieldMaskPolicy> FieldPolicies);
