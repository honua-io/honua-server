// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IRasterPointReader
{
    Task<ImmutableArray<ProjectedPoint>> QueryProjectedPointsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
