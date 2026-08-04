// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Signals that a feature write was rejected by a read-only provider before any row could be
/// changed. Unlike a general <see cref="NotSupportedException"/>, this exception is explicit proof
/// that retrying cannot duplicate a partially committed write.
/// </summary>
public sealed class ReadOnlyFeatureWriteException : NotSupportedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyFeatureWriteException"/> class.
    /// </summary>
    /// <param name="message">The provider-specific rejection message.</param>
    public ReadOnlyFeatureWriteException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Feature writer that rejects all write operations with <see cref="ReadOnlyFeatureWriteException"/>.
/// Registered by read-only feature providers (DuckDB, MySQL/MariaDB) so DI consumers that
/// require <see cref="IFeatureWriter"/> can activate while the slice remains read/query-only.
/// </summary>
public sealed class ReadOnlyFeatureWriter : IFeatureWriter
{
    private readonly string _providerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyFeatureWriter"/> class.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the read-only provider, used in the rejection message
    /// (for example <c>"DuckDB"</c> or <c>"MySQL/MariaDB"</c>).
    /// </param>
    public ReadOnlyFeatureWriter(string providerName)
    {
        _providerName = providerName;
    }

    /// <inheritdoc />
    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Feature creation is not supported.");

    /// <inheritdoc />
    public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Feature updates are not supported.");

    /// <inheritdoc />
    public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Feature deletion is not supported.");

    /// <inheritdoc />
    public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Batch edits are not supported.");
}
