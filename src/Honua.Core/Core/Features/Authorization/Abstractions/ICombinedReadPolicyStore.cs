// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Optional capability for a row-policy store that can read fresh row and field policies
/// together when the registered field store is the compatible pair.
/// </summary>
public interface ICombinedReadPolicyStore
{
    /// <summary>
    /// Returns whether <paramref name="fieldStore"/> shares this store's provider and catalog schema.
    /// </summary>
    /// <param name="fieldStore">The field-mask store registered for the same read.</param>
    /// <returns><see langword="true"/> when one catalog batch can serve both policy sets.</returns>
    bool CanResolveWith(IFieldMaskPolicyStore fieldStore);

    /// <summary>
    /// Reads the current row and field policies for every service scope in one catalog batch.
    /// Evaluated policies are not cached.
    /// </summary>
    /// <param name="fieldStore">The compatible field-mask store.</param>
    /// <param name="roles">Caller roles. Wildcard role policies still apply when this is empty.</param>
    /// <param name="services">Publication service names. An empty set matches wildcard service policies.</param>
    /// <param name="layer">Layer name the policies are resolved for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fresh row and field policies for this read.</returns>
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
