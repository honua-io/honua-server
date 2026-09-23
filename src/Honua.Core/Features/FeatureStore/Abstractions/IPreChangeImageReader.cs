// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional feature-reader capability: evaluates a query against the row images the change log
/// recorded before a change, rather than against the live rows. Replica delivery uses it to judge
/// whether a row was inside the replica scope and visible to the caller before it was updated or
/// deleted (#4879). A reader that does not implement it leaves replica delivery on its
/// current-state-only classification.
/// </summary>
public interface IPreChangeImageReader
{
    /// <summary>
    /// Returns the object ids whose pre-change image, recorded by one of <paramref name="changeIds"/>,
    /// matches <paramref name="query"/>'s filters. The provider applies the same enforced read policy
    /// (permanent filter and row-level security) it applies to a live read of the layer, so a row the
    /// caller could not read is never reported.
    /// </summary>
    /// <param name="layerId">Storage layer id the changes belong to.</param>
    /// <param name="query">Filters to evaluate (where clause and spatial filter); paging is ignored.</param>
    /// <param name="changeIds">Change-log ids whose recorded pre-change images are evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching object ids.</returns>
    Task<IReadOnlySet<long>> QueryPreChangeObjectIdsAsync(
        int layerId,
        FeatureQuery query,
        IReadOnlyCollection<long> changeIds,
        CancellationToken cancellationToken = default);
}
