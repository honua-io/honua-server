// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Services;

public sealed class TemporalRollbackRunner : ITemporalRollbackRunner
{
    private readonly ITemporalHistoryStore _historyStore;
    private readonly IFeatureWriter _featureWriter;
    private readonly IChangeTracker _changeTracker;
    private readonly ITemporalCorrectiveJobSink _jobSink;

    public TemporalRollbackRunner(
        ITemporalHistoryStore historyStore,
        IFeatureWriter featureWriter,
        IChangeTracker changeTracker,
        ITemporalCorrectiveJobSink jobSink)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _featureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        _changeTracker = changeTracker ?? throw new ArgumentNullException(nameof(changeTracker));
        _jobSink = jobSink ?? throw new ArgumentNullException(nameof(jobSink));
    }

    public async Task<TemporalRollbackJobHandle> SubmitRollbackAsync(
        string serviceId,
        int layerId,
        int storageLayerId,
        ResolvedTemporalCheckpoint target,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var currentGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);

        // Snapshot the corrective object ids at submission time so the background job re-reads a stable
        // window. The corrective deletes target features inserted after the target checkpoint.
        var correctiveDeletes = await ComputeCorrectiveDeletesAsync(
            storageLayerId, target.Generation, currentGeneration, cancellationToken)
            .ConfigureAwait(false);

        var (jobId, status) = await _jobSink
            .SubmitAsync(
                operationName: "temporal.rollback",
                serviceId: serviceId,
                layerId: layerId,
                work: ct => ApplyCorrectiveEditsAsync(storageLayerId, correctiveDeletes, ct),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new TemporalRollbackJobHandle(
            JobId: jobId,
            ServiceId: serviceId,
            LayerId: layerId,
            TargetCheckpoint: target,
            Status: status);
    }

    private async Task<ImmutableArray<long>> ComputeCorrectiveDeletesAsync(
        int storageLayerId,
        long targetGeneration,
        long currentGeneration,
        CancellationToken cancellationToken)
    {
        if (targetGeneration >= currentGeneration)
        {
            return ImmutableArray<long>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<long>();
        long afterObjectId = 0;
        const int pageSize = 1000;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await _historyStore
                .GetChangesInWindowAsync(storageLayerId, targetGeneration, currentGeneration, afterObjectId, pageSize, cancellationToken)
                .ConfigureAwait(false);
            if (rows.Count == 0)
            {
                break;
            }

            long? lastObjectId = null;
            TemporalChangeKind firstOp = default;
            TemporalChangeKind lastOp = default;

            void Flush(long? id)
            {
                if (id is not { } objectId)
                {
                    return;
                }

                // A net insert since the target checkpoint is undone by deleting the feature now.
                if (firstOp == TemporalChangeKind.Insert && lastOp != TemporalChangeKind.Delete)
                {
                    builder.Add(objectId);
                }
            }

            foreach (var row in rows)
            {
                if (lastObjectId != row.ObjectId)
                {
                    Flush(lastObjectId);
                    lastObjectId = row.ObjectId;
                    firstOp = row.Operation;
                }

                lastOp = row.Operation;
            }

            Flush(lastObjectId);

            afterObjectId = rows[^1].ObjectId;
            if (rows.Count < pageSize)
            {
                break;
            }
        }

        return builder.ToImmutable();
    }

    private async Task ApplyCorrectiveEditsAsync(
        int storageLayerId,
        ImmutableArray<long> correctiveDeletes,
        CancellationToken cancellationToken)
    {
        if (correctiveDeletes.IsDefaultOrEmpty)
        {
            return;
        }

        // The corrective edit is a NEW forward delete batch through the canonical edit pipeline. The
        // change-tracking trigger records these as new change-log rows and advances the generation, so the
        // rollback produces a new checkpoint and leaves prior history intact.
        var batch = FeatureEditBatch.Create(deletes: correctiveDeletes, rollbackOnFailure: true);
        _ = await _featureWriter.ApplyEditsAsync(storageLayerId, batch, cancellationToken).ConfigureAwait(false);
    }
}
