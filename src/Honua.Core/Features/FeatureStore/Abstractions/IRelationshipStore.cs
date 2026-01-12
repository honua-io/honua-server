// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides relationship query capabilities for related features.
/// Segregated to support specialized relationship handling and caching.
/// </summary>
public interface IRelationshipStore
{
    /// <summary>
    /// Queries features related to the specified source features through a relationship
    /// </summary>
    /// <param name="layerId">Source layer identifier</param>
    /// <param name="query">Related query specification including object IDs and relationship</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result containing related features</returns>
    Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default);
}
