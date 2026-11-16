// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IFeatureWriter
{
    Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This feature writer does not support cross-layer transactions.");

    Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default);

    Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default);

    // Resolves the storage-layer id from the resource's primary storage binding through the graph
    // provider, then dispatches to the int-keyed overload. A default-interface method, so implementations
    // need no change. Throws InvalidOperationException when the resource has no resolvable
    // storage-layer id (for example a document-only resource).
    async Task<FeatureEditResult> ApplyEditsAsync(
        IMetadataV2GraphProvider graphProvider,
        MetadataV2Object resource,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graphProvider);
        ArgumentNullException.ThrowIfNull(resource);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = snapshot.ResolveStorageLayerId(resource)
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Metadata.Id}' has no resolvable storage-layer id.");
        return await ApplyEditsAsync(storageLayerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    Task<FeatureEditApplication> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        FeatureEditIdempotencyReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        _ = receipt;
        return ApplyWithoutDurableReceiptAsync();

        async Task<FeatureEditApplication> ApplyWithoutDurableReceiptAsync()
        {
            var result = await ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
            return new FeatureEditApplication { Result = result, ReceiptDurable = false };
        }
    }
}

public interface IFeatureWriterTransaction : IAsyncDisposable
{
    bool SupportsDurableReceipts => false;

    Task<FeatureWriterTransactionCommitOutcome> CommitAsync(
        FeatureWriterTransactionReceipt receipt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This writer transaction does not support durable receipts.");

    Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default);

    Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public enum FeatureWriterTransactionCommitOutcome
{
    Committed,

    Unknown,

    SupersededByExistingReceipt
}
