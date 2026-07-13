// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Canonical replica-upload synchronization pipeline (#1272). See <see cref="IReplicaSyncService"/>.
/// </summary>
/// <remarks>
/// Conflict detection compares each uploaded update/delete against the server change log since the
/// replica's base generation: a server-side mutation of the same <c>(layerId, objectId)</c> indicates
/// a concurrent server edit and produces a conflict candidate. Non-conflicting edits (and, under
/// last-write-wins, conflicting edits) are applied through <see cref="IReplicaEditApplier"/>, which
/// the protocol adapter backs with the shared edit pipeline. Durable conflict records are written
/// through <see cref="IReplicaConflictRepository"/> when <see cref="IReplicaConflictRepository.SupportsConflictReview"/>
/// is true; when conflict review is unsupported the service falls back to last-write-wins so existing
/// non-manual strategy behavior is preserved (conflict-record creation + resolution API is owned by
/// #1287).
/// </remarks>
public sealed partial class ReplicaSyncService : IReplicaSyncService
{
    // PA-020: replica conflict detection / edit application is a hot path (every replica
    // upload) and previously had no telemetry span.
    private static readonly ActivitySource _activitySource = new("Honua.Core.FeatureStore");

    private readonly IChangeTracker _changeTracker;
    private readonly IReplicaConflictRepository _conflictRepository;
    private readonly ILogger<ReplicaSyncService> _logger;

    /// <summary>
    /// Initializes a new <see cref="ReplicaSyncService"/>.
    /// </summary>
    /// <param name="changeTracker">Change-log reader used for conflict detection.</param>
    /// <param name="conflictRepository">Durable conflict-record store (#1287 owns the resolution API).</param>
    /// <param name="logger">Logger.</param>
    public ReplicaSyncService(
        IChangeTracker changeTracker,
        IReplicaConflictRepository conflictRepository,
        ILogger<ReplicaSyncService> logger)
    {
        _changeTracker = changeTracker ?? throw new ArgumentNullException(nameof(changeTracker));
        _conflictRepository = conflictRepository ?? throw new ArgumentNullException(nameof(conflictRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ReplicaSyncUploadReport> ApplyUploadAsync(
        ReplicaSyncRequest request,
        IReplicaEditApplier editApplier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editApplier);

        var layerEdits = request.LayerEdits;

        using var activity = _activitySource.StartActivity("replicasync.apply_upload");
        activity?.SetTag("replica.id", request.ReplicaId);
        activity?.SetTag("service.id", request.ServiceId);
        activity?.SetTag("replicasync.layer_count", layerEdits.IsDefault ? 0 : layerEdits.Length);

        if (layerEdits.IsDefaultOrEmpty)
        {
            var currentGen = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
            return new ReplicaSyncUploadReport(
                Success: true,
                AppliedAdds: 0,
                AppliedUpdates: 0,
                AppliedDeletes: 0,
                Conflicts: ImmutableArray<ReplicaSyncConflict>.Empty,
                LayerResults: ImmutableArray<ReplicaLayerApplyResult>.Empty,
                ServerGeneration: currentGen,
                FailureMessage: null);
        }

        // Conflict review is supported only when the provider can durably persist conflicts. When it
        // cannot (read-only providers, or before #1287 lands), fall back to last-write-wins so the
        // existing non-manual strategy behavior is preserved.
        var canRecordConflicts = _conflictRepository.SupportsConflictReview;
        var applyConflicting = request.LastWriteWins || !canRecordConflicts;

        var conflicts = ImmutableArray.CreateBuilder<ReplicaSyncConflict>();
        var layerResults = ImmutableArray.CreateBuilder<ReplicaLayerApplyResult>(layerEdits.Length);
        var totalAdds = 0;
        var totalUpdates = 0;
        var totalDeletes = 0;
        var anyFailure = false;
        string? firstFailure = null;

        foreach (var layer in layerEdits)
        {
            var edits = layer.Edits.IsDefault ? ImmutableArray<ReplicaUploadEdit>.Empty : layer.Edits;
            if (edits.IsEmpty)
            {
                layerResults.Add(new ReplicaLayerApplyResult(layer.PublicLayerId, 0, 0, 0, Failed: false, FailureMessage: null));
                continue;
            }

            // Map server-side mutations since the base generation for conflict detection. Only
            // update/delete uploads can conflict; inserts use server-assigned ids and cannot collide
            // on an existing (layerId, objectId) key here (duplicate-insert detection is owned by the
            // edit pipeline's uniqueness handling and #1287).
            var uploadedObjectIds = new HashSet<long>(edits
                .Where(edit => edit.Kind != FeatureEditOperationKind.Create && edit.ObjectId.HasValue)
                .Select(edit => edit.ObjectId!.Value));

            // Only the uploaded ids can ever be probed below, so push the id filter into the change
            // tracker: providers that support it (Postgres) restrict the change-log scan in SQL, and
            // the interface default filters client-side. Either way a long-offline replica's upload
            // never materializes the entire change history since the base generation.
            var serverByObjectId = new Dictionary<long, FeatureChangeOperation>();
            if (uploadedObjectIds.Count > 0)
            {
                var serverChanges = await _changeTracker
                    .GetChangesSinceAsync(request.BaseGeneration, [layer.StorageLayerId], uploadedObjectIds, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var change in serverChanges)
                {
                    serverByObjectId[change.ObjectId] = change.Operation;
                }
            }

            var editsToApply = ImmutableArray.CreateBuilder<ReplicaUploadEdit>(edits.Length);
            foreach (var edit in edits)
            {
                if (edit.Kind != FeatureEditOperationKind.Create &&
                    edit.ObjectId is { } objectId &&
                    serverByObjectId.TryGetValue(objectId, out var serverOp))
                {
                    var conflictType = ClassifyConflict(edit.Kind, serverOp);
                    // In manual-review mode the conflicting edit is NOT applied, so the durable
                    // conflict record is the only carrier of the client intent. If that write fails
                    // it must surface (propagate) rather than be swallowed: silently skipping the
                    // edit would lose it with no record to resolve. Under last-write-wins the edit is
                    // still applied below, so a record failure stays tolerable (logged only).
                    var conflictId = canRecordConflicts
                        ? await RecordConflictAsync(request, layer.PublicLayerId, objectId, conflictType, mustRecord: !applyConflicting, cancellationToken).ConfigureAwait(false)
                        : null;

                    conflicts.Add(new ReplicaSyncConflict(
                        layer.PublicLayerId,
                        objectId,
                        conflictType,
                        edit.Kind,
                        serverOp,
                        Applied: applyConflicting,
                        ConflictId: conflictId));

                    if (!applyConflicting)
                    {
                        // Manual review: skip the conflicting edit. The conflict record carries the
                        // client intent for later resolution.
                        continue;
                    }
                }

                editsToApply.Add(edit);
            }

            var applyResult = editsToApply.Count == 0
                ? new ReplicaLayerApplyResult(layer.PublicLayerId, 0, 0, 0, Failed: false, FailureMessage: null)
                : await editApplier
                    .ApplyAsync(request.ServiceId, layer.PublicLayerId, editsToApply.ToImmutable(), request.RollbackOnFailure, cancellationToken)
                    .ConfigureAwait(false);

            totalAdds += applyResult.AppliedAdds;
            totalUpdates += applyResult.AppliedUpdates;
            totalDeletes += applyResult.AppliedDeletes;
            if (applyResult.Failed)
            {
                anyFailure = true;
                firstFailure ??= applyResult.FailureMessage;
            }

            layerResults.Add(applyResult);
        }

        if (conflicts.Count > 0)
        {
            Log.ConflictsDetected(_logger, request.ReplicaId, request.ServiceId, conflicts.Count, applyConflicting);
        }

        activity?.SetTag("replicasync.conflict_count", conflicts.Count);
        if (anyFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, firstFailure);
        }

        // The server generation produced once the uploaded edits applied. A subsequent download delta
        // uses this as its lower bound so the replica does not receive its own edits back.
        var serverGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);

        return new ReplicaSyncUploadReport(
            Success: !anyFailure,
            AppliedAdds: totalAdds,
            AppliedUpdates: totalUpdates,
            AppliedDeletes: totalDeletes,
            Conflicts: conflicts.ToImmutable(),
            LayerResults: layerResults.ToImmutable(),
            ServerGeneration: serverGeneration,
            FailureMessage: firstFailure);
    }

    private static ReplicaConflictType ClassifyConflict(
        FeatureEditOperationKind clientKind,
        FeatureChangeOperation serverOperation)
        => (clientKind, serverOperation) switch
        {
            (FeatureEditOperationKind.Delete, FeatureChangeOperation.Update) => ReplicaConflictType.DeleteUpdate,
            (FeatureEditOperationKind.Update, FeatureChangeOperation.Delete) => ReplicaConflictType.UpdateDelete,
            // Update vs concurrent server insert/update: the coarse attribute classification. The sync
            // service sees only operation kinds, not values, so it cannot distinguish a geometry-only
            // conflict here. The protocol adapter refines this to ReplicaConflictType.Geometry once it
            // has captured the client/server states (ReplicaConflictClassifier, #1287). DuplicateInsert,
            // Attachment, and Relationship remain undetected: inserts use server-assigned ids, and the
            // replica upload model carries no attachment/related-record inputs to detect them.
            _ => ReplicaConflictType.Attribute,
        };

    private async Task<string?> RecordConflictAsync(
        ReplicaSyncRequest request,
        int publicLayerId,
        long objectId,
        ReplicaConflictType conflictType,
        bool mustRecord,
        CancellationToken cancellationToken)
    {
        var conflictId = Guid.NewGuid().ToString("N");
        var record = new ReplicaConflictRecord
        {
            ConflictId = conflictId,
            ReplicaId = request.ReplicaId,
            ServiceId = request.ServiceId,
            LayerId = publicLayerId,
            ObjectId = objectId,
            ConflictType = conflictType,
            Status = ReplicaConflictStatus.Pending,
            SyncOperationId = request.SyncOperationId,
            DeviceId = request.DeviceId,
            UserId = request.UserId,
            ServerGeneration = request.BaseGeneration,
            DetectedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await _conflictRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            return conflictId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Always surface the failure in logs for operator follow-up.
            Log.ConflictRecordFailed(_logger, request.ReplicaId, publicLayerId, objectId, ex);

            // Manual review (mustRecord): the conflicting edit will be skipped, so the conflict
            // record is the only carrier of the client intent. Losing it would drop the edit with
            // no trace, so propagate the failure and fail the sync instead of returning null.
            if (mustRecord)
            {
                throw;
            }

            // Last-write-wins: the edit is still applied, so a record write failure is tolerable.
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 7740, Level = LogLevel.Information,
            Message = "Replica {ReplicaId} sync for service {ServiceId} detected {ConflictCount} conflict(s); appliedConflicting={AppliedConflicting}")]
        public static partial void ConflictsDetected(ILogger logger, string replicaId, string serviceId, int conflictCount, bool appliedConflicting);

        [LoggerMessage(EventId = 7741, Level = LogLevel.Warning,
            Message = "Failed to persist replica conflict record for replica {ReplicaId} layer {LayerId} objectId {ObjectId}")]
        public static partial void ConflictRecordFailed(ILogger logger, string replicaId, int layerId, long objectId, Exception exception);
    }
}
