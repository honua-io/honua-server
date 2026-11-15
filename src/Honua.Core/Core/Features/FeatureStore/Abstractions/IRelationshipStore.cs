// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides relationship query capabilities for related features.
/// Segregated to support specialized relationship handling and caching.
/// </summary>
public interface IRelationshipStore
{
    bool SupportsRelatedPages => false;

    Task<RelatedFeaturePage> QueryRelatedPageAsync(int layerId, RelatedPageQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The provider does not support bounded relationship pages.");

    /// <summary>
    /// Queries features related to the specified source features through a relationship
    /// </summary>
    /// <param name="layerId">Source layer identifier</param>
    /// <param name="query">Related query specification including object IDs and relationship</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result containing related features</returns>
    Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default);
}
