// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IFeatureLayerCopyService
{
    Task<FeatureLayerCopyResult> CopyAsync(int sourceLayerId, string targetLayerName,
        FeatureQuery query, string operationId, long maxBytes, CancellationToken cancellationToken);
}

public sealed record FeatureLayerCopyResult(int LayerId, long FeatureCount, int Srid);
