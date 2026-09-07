// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>Copies a canonical filtered feature stream into a new published layer.</summary>
public interface IFeatureLayerCopyService
{
    /// <summary>Creates a distinct target, retaining source schema, CRS and access policy.</summary>
    Task<FeatureLayerCopyResult> CopyAsync(int sourceLayerId, string targetLayerName,
        FeatureQuery query, string operationId, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>Identifies the committed copy and its source provenance.</summary>
/// <param name="LayerId">New canonical layer handle.</param>
/// <param name="FeatureCount">Exact number of copied rows.</param>
/// <param name="Srid">Copied spatial reference.</param>
public sealed record FeatureLayerCopyResult(int LayerId, long FeatureCount, int Srid);
