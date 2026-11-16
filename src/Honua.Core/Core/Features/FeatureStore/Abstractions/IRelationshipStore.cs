// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IRelationshipStore
{
    bool SupportsRelatedPages => false;

    Task<RelatedFeaturePage> QueryRelatedPageAsync(int layerId, RelatedPageQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The provider does not support bounded relationship pages.");

    Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default);
}
