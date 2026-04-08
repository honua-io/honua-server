// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.DuckDB.Features.FeatureStore;

/// <summary>
/// Feature writer that rejects all write operations.
/// Registered when the DuckDB provider is active since DuckDB is read-only in V1.
/// </summary>
internal sealed class ReadOnlyFeatureWriter : IFeatureWriter
{
    /// <inheritdoc />
    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DuckDB provider is read-only. Feature creation is not supported.");
    }

    /// <inheritdoc />
    public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DuckDB provider is read-only. Feature updates are not supported.");
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DuckDB provider is read-only. Feature deletion is not supported.");
    }

    /// <inheritdoc />
    public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DuckDB provider is read-only. Batch edits are not supported.");
    }
}
