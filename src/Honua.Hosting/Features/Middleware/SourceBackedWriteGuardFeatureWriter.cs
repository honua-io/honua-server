// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Middleware;

// The managed writer stores features in the shared features table. A layer published over a
// source table reads that table instead, so a write accepted for it would never be read back.
// This decorator refuses such writes at the shared edit boundary, for every protocol and
// background writer, the way the read-only providers refuse theirs.
public sealed class SourceBackedWriteGuardFeatureWriter(IFeatureWriter inner, IMetadataV2GraphProvider metadata)
    : IFeatureWriter
{
    public async Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var transaction = await inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return new GuardedTransaction(this, transaction);
    }

    public async Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        await EnsureWritableAsync(layerId, cancellationToken).ConfigureAwait(false);
        return await inner.CreateAsync(layerId, feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        await EnsureWritableAsync(layerId, cancellationToken).ConfigureAwait(false);
        return await inner.UpdateAsync(layerId, feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        await EnsureWritableAsync(layerId, cancellationToken).ConfigureAwait(false);
        return await inner.DeleteAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
    {
        await EnsureWritableAsync(layerId, cancellationToken).ConfigureAwait(false);
        return await inner.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureEditApplication> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        FeatureEditIdempotencyReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        await EnsureWritableAsync(layerId, cancellationToken).ConfigureAwait(false);
        return await inner.ApplyEditsAsync(layerId, editBatch, receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureWritableAsync(int layerId, CancellationToken cancellationToken)
    {
        MetadataV2GraphSnapshot snapshot;
        try
        {
            snapshot = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            // A host wired for storage-only access has no graph, so no layer can be bound to a source table.
            return;
        }

        // The message stays generic: some surfaces return it to the caller, and the table name is internal.
        if (FindSourceTable(snapshot, layerId) is not null)
        {
            throw new ReadOnlyFeatureWriteException(
                "This layer is published over a source table, which this server cannot write through. " +
                "Edit the source table directly and refresh the layer, or republish the data into the managed feature store.");
        }
    }

    // The source table behind this storage layer when every publication of it is bound to one, or null when
    // the managed writer serves it (managed binding, no binding, or no publication at all).
    internal static string? FindSourceTable(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        string? source = null;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (snapshot.ResolveStorageLayerId(publication, resource: null) != layerId)
            {
                continue;
            }

            var table = ManagedWriteStorage.SourceTableOf(snapshot, publication);
            if (table is null)
            {
                return null;
            }

            source ??= table;
        }

        return source;
    }

    private sealed class GuardedTransaction(SourceBackedWriteGuardFeatureWriter owner, IFeatureWriterTransaction transaction)
        : IFeatureWriterTransaction
    {
        public bool SupportsDurableReceipts => transaction.SupportsDurableReceipts;

        public async Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            await owner.EnsureWritableAsync(layerId, cancellationToken).ConfigureAwait(false);
            return await transaction.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
        }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(
            FeatureWriterTransactionReceipt receipt,
            CancellationToken cancellationToken = default)
            => transaction.CommitAsync(receipt, cancellationToken);

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
