// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional branch-read capability of a reader after publication storage binding.
/// Implementations share eligibility with their versioned query validation; a host-wide
/// version manager alone does not establish that a particular publication is versioned.
/// </summary>
public interface IBranchVersioningFeatureReader
{
    /// <summary>
    /// Determines whether this bound reader can apply branch overlays, without querying
    /// feature rows. Credential resolution may be needed to identify the bound database.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel capability resolution.</param>
    /// <returns>True only when this reader's storage supports branch-versioned reads.</returns>
    Task<bool> SupportsBranchVersioningAsync(CancellationToken cancellationToken = default);
}
