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
        IReplicaServerStateCapturer? serverStateCapturer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editApplier);
        var serverStates = new Dictionary<(int PublicLayerId, long ObjectId), string>();

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
                FailureMessage: null,
                ServerStates: serverStates);
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
            var editIndex = -1;
            // Indexes into `conflicts` for this layer whose client edit was dispatched, so the
            // recorded/reported "the client edit landed" flag can be corrected from the layer's actual
            // apply outcome below. `layerConflictIds` covers every conflict on the layer, including the
            // ones manual review withheld, because all of them need the resolution-base generation.
            var layerConflictIndexes = new List<int>();
            // Request slot (position in the edit array handed to the applier) of each dispatched
            // conflicting edit, parallel to layerConflictIndexes, so per-row outcomes map back to the
            // exact edit rather than to an object id several edits may share (#2430).
            var layerConflictSlots = new List<int>();
            var layerConflictIds = new List<string>();
            // Conflicts manual review withheld: they are never dispatched, so they have no request
            // slot, but their server state still has to be captured for the review surface.
            var layerWithheldObjectIds = new List<long>();
            var layerConflictCount = 0;
            foreach (var edit in edits)
            {
                editIndex++;
                if (edit.Kind != FeatureEditOperationKind.Create &&
                    edit.ObjectId is { } objectId &&
                    serverByObjectId.TryGetValue(objectId, out var serverOp))
                {
                    var conflictType = ClassifyConflict(edit.Kind, serverOp);
                    // The record is written BEFORE the edit batch runs, so it is recorded
                    // conservatively as "client edit not applied" and only promoted below once the
                    // layer's batch is known to have committed. Claiming the client state landed when
                    // the batch later failed (validation, provider error, or a rollbackOnFailure
                    // rollback triggered by another row) would make a later acceptClient resolve to a
                    // no-op against a state that never existed.
                    //
                    // In manual-review mode the conflicting edit is deliberately NOT applied, so the
                    // durable conflict record is the only carrier of the client intent. If that write
                    // fails it must surface (propagate) rather than be swallowed: silently skipping
                    // the edit would lose it with no record to resolve. Under last-write-wins the edit
                    // is still applied below, so a record failure stays tolerable (logged only).
                    var conflictId = canRecordConflicts
                        ? await RecordConflictAsync(request, layer, objectId, conflictType, mustRecord: !applyConflicting, cancellationToken).ConfigureAwait(false)
                        : null;

                    layerConflictCount++;
                    if (conflictId is { Length: > 0 })
                    {
                        layerConflictIds.Add(conflictId);
                    }

                    if (applyConflicting)
                    {
                        layerConflictIndexes.Add(conflicts.Count);
                        layerConflictSlots.Add(editsToApply.Count);
                    }
                    else
                    {
                        layerWithheldObjectIds.Add(objectId);
                    }

                    conflicts.Add(new ReplicaSyncConflict(
                        layer.PublicLayerId,
                        objectId,
                        conflictType,
                        edit.Kind,
                        serverOp,
                        Applied: false,
                        ConflictId: conflictId,
                        EditIndex: editIndex));

                    if (!applyConflicting)
                    {
                        // Manual review: skip the conflicting edit. The conflict record carries the
                        // client intent for later resolution.
                        continue;
                    }
                }

                editsToApply.Add(edit);
            }

            // Watermark taken BEFORE the snapshot and the batch. Sampling it after the capture would
            // place an edit that landed in between inside the window while leaving it out of the
            // snapshot, and the staleness probe — which starts from this generation — would then see
            // nothing newer and let a resolution overwrite that edit (#2430).
            var preBatchGeneration = layerConflictCount > 0
                ? await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false)
                : 0L;

            // Snapshot the server side of every conflicting feature HERE: after detection, before the
            // uploaded edits apply. Capturing earlier let a server edit landing in between be flagged
            // as the conflict yet be missing from the snapshot, so a later keep-server resolution
            // restored a state that silently discarded it — and the collapsed change log makes that
            // undetectable after the fact (#2430).
            if (serverStateCapturer is not null && layerConflictCount > 0)
            {
                var targets = layerConflictIndexes
                    .Select(index => conflicts[index])
                    .Select(conflict => new ReplicaConflictCaptureTarget(
                        layer.PublicLayerId, layer.StorageLayerId, conflict.ObjectId))
                    .Concat(layerWithheldObjectIds.Select(objectId => new ReplicaConflictCaptureTarget(
                        layer.PublicLayerId, layer.StorageLayerId, objectId)))
                    .Distinct()
                    .ToImmutableArray();

                var captured = await serverStateCapturer.CaptureAsync(targets, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in captured)
                {
                    serverStates[entry.Key] = entry.Value;
                }
            }

            var applyResult = editsToApply.Count == 0
                ? new ReplicaLayerApplyResult(layer.PublicLayerId, 0, 0, 0, Failed: false, FailureMessage: null)
                : await editApplier
                    .ApplyAsync(request.ServiceId, layer.PublicLayerId, editsToApply.ToImmutable(), request.RollbackOnFailure, cancellationToken)
                    .ConfigureAwait(false);

            // Watermark taken IMMEDIATELY after the batch, before any other await. The collapsed change
            // feed reports only the latest change per object, so a foreign edit landing between our
            // commit and the change-log probe would otherwise be read as the generation our own edit
            // produced — and a staleness check starting after it would never see it (#2430). Clamping
            // to this watermark keeps any change recorded after the batch outside every conflict's
            // base, and can only lower a base, never raise it past our own committed edit.
            // Uncancellable for the same reason the detection-state writes below are: the batch has
            // already committed, and a client disconnect here would skip straight past the base
            // generation and applied-state updates, leaving a last-write-wins conflict recorded as
            // not-applied with no base — which lets a later keepServer finalize as a no-op while the
            // committed client overwrite is still in place (#2430).
            var postBatchGeneration = layerConflictCount > 0
                ? await _changeTracker.GetCurrentGenerationAsync(CancellationToken.None).ConfigureAwait(false)
                : 0L;

            totalAdds += applyResult.AppliedAdds;
            totalUpdates += applyResult.AppliedUpdates;
            totalDeletes += applyResult.AppliedDeletes;
            if (applyResult.Failed)
            {
                anyFailure = true;
                firstFailure ??= applyResult.FailureMessage;
            }

            if (layerConflictCount > 0)
            {
                var baseGenerations = await ResolveConflictBaseGenerationsAsync(
                        layer, conflicts, layerConflictIndexes, preBatchGeneration, postBatchGeneration, CancellationToken.None)
                    .ConfigureAwait(false);
                // The edit batch has already committed at this point, so the detection state that
                // describes it must not be abandoned if the client disconnects: a record left without
                // its applied flag or base generation would, after the settle window, let keepServer
                // plan a no-op while the client edit is in fact committed (#2430).
                await MarkConflictsAppliedAsync(
                        conflicts,
                        layerConflictIndexes,
                        layerConflictSlots,
                        layerConflictIds,
                        applyResult,
                        baseGenerations,
                        preBatchGeneration,
                        canRecordConflicts,
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
            FailureMessage: firstFailure,
            ServerStates: serverStates);
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

    /// <summary>
    /// Promotes each conflict whose own uploaded edit committed to "the client edit landed", on both
    /// the transient report and the durable record. Recorded after the fact so the flag describes what
    /// actually committed rather than the requested conflict policy (#2430).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attribution is <b>per edit</b>, not per layer. With <c>rollbackOnFailure=false</c> the shared
    /// edit pipeline commits rows independently, so one conflicting edit can land while an unrelated
    /// sibling in the same batch fails and marks the whole layer failed. Promoting off the layer flag
    /// would leave a committed client overwrite recorded as not-applied, and a later keep-server
    /// resolution would then plan a no-op and mark the conflict resolved with the overwrite still in
    /// place. Only ids the applier reports as committed are promoted; a conflict the applier cannot
    /// attribute keeps the conservative <c>false</c>, which makes a later accept-client a real write.
    /// </para>
    /// <para>
    /// The durable write is the guarded detection-state update, never a whole-record upsert: an
    /// operator can resolve a freshly listed conflict while this post-processing is still running, and
    /// rewriting the record from a stale read would reopen that resolution.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Position of an operation kind in the shared edit pipeline's fixed execution order. The pipeline
    /// groups a batch into creates, then updates, then deletes rather than honouring the order the
    /// operations were listed in, so this — not the request slot — is what decides which of several
    /// edits to the same object leaves its state in the row.
    /// </summary>
    private static int ExecutionRank(FeatureEditOperationKind kind) => kind switch
    {
        FeatureEditOperationKind.Create => 0,
        FeatureEditOperationKind.Update => 1,
        _ => 2,
    };

    private async Task MarkConflictsAppliedAsync(
        ImmutableArray<ReplicaSyncConflict>.Builder conflicts,
        List<int> layerConflictIndexes,
        List<int> layerConflictSlots,
        List<string> layerConflictIds,
        ReplicaLayerApplyResult applyResult,
        IReadOnlyDictionary<string, long> baseGenerations,
        long preBatchGeneration,
        bool canRecordConflicts,
        CancellationToken cancellationToken)
    {
        // Matched by request slot, not by object id. One payload can carry several operations for the
        // same object — an update and a delete, say — and with rollbackOnFailure=false one can commit
        // while the other fails. Object identity cannot say which landed, so attributing by id marked
        // the wrong conflict applied and a later resolution planned against a state that never
        // committed (#2430).
        var committedSlots = applyResult.CommittedEditIndexes.IsDefaultOrEmpty
            ? []
            : new HashSet<int>(applyResult.CommittedEditIndexes);

        // Slots the writer could not classify. Kept separate from committedSlots: an indeterminate row
        // is neither "landed" nor "did not land", and recording it as either lets one of the two
        // resolution shortcuts report a state the row may not hold (#2430).
        var indeterminateSlots = applyResult.IndeterminateEditIndexes.IsDefaultOrEmpty
            ? []
            : new HashSet<int>(applyResult.IndeterminateEditIndexes);

        // Only the edit that EXECUTES last for a given object leaves its client state in the row.
        // Ranked by execution order rather than by request slot: the shared edit pipeline groups a
        // batch into creates, then updates, then deletes, so an upload listing delete(5) before
        // update(5) still ends with the row deleted. Ranking by slot promoted the update, and an
        // acceptClient on it then finalized as a no-op while the feature was in fact gone (#2430).
        var lastCommittedByObject = new Dictionary<long, (int Rank, int Slot)>();
        for (var i = 0; i < layerConflictIndexes.Count; i++)
        {
            var slot = layerConflictSlots[i];
            if (!committedSlots.Contains(slot))
            {
                continue;
            }

            var conflict = conflicts[layerConflictIndexes[i]];
            var candidate = (Rank: ExecutionRank(conflict.ClientKind), Slot: slot);
            if (!lastCommittedByObject.TryGetValue(conflict.ObjectId, out var current) ||
                candidate.Rank > current.Rank ||
                (candidate.Rank == current.Rank && candidate.Slot > current.Slot))
            {
                lastCommittedByObject[conflict.ObjectId] = candidate;
            }
        }

        var promoted = new HashSet<string>(StringComparer.Ordinal);
        var indeterminate = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < layerConflictIndexes.Count; i++)
        {
            var index = layerConflictIndexes[i];
            var conflict = conflicts[index];

            if (indeterminateSlots.Contains(layerConflictSlots[i]) &&
                conflict.ConflictId is { Length: > 0 } unknownId)
            {
                indeterminate.Add(unknownId);
            }
            var winner = lastCommittedByObject.GetValueOrDefault(conflict.ObjectId, (Rank: -1, Slot: -1));
            if (winner.Rank != ExecutionRank(conflict.ClientKind) || winner.Slot != layerConflictSlots[i])
            {
                continue;
            }

            conflicts[index] = conflict with { Applied = true };

            if (conflict.ConflictId is { Length: > 0 } promotedId)
            {
                promoted.Add(promotedId);
            }
        }

        if (!canRecordConflicts)
        {
            return;
        }

        // Every conflict on the layer gets the resolution-base generation, including the ones manual
        // review withheld; only the ones whose own edit committed also get the applied flag.
        foreach (var conflictId in layerConflictIds)
        {
            try
            {
                await _conflictRepository.TryUpdateDetectionStateAsync(
                        new ReplicaConflictDetectionUpdate(
                            conflictId,
                            ConflictType: null,
                            ClientStateJson: null,
                            ServerStateJson: null,
                            ClientEditApplied: promoted.Contains(conflictId) ? true : null,
                            ClientEditOutcomeUnknown: indeterminate.Contains(conflictId) ? true : null,
                            ResolutionBaseGeneration: baseGenerations.TryGetValue(conflictId, out var generation)
                                ? generation
                                : preBatchGeneration),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Do NOT swallow this. The edit committed, so leaving the record saying otherwise is
                // not a safe conservative state: a later keep-server resolution would plan a no-op and
                // mark itself resolved while the client overwrite is still in place. Failing the sync
                // loudly makes the divergence visible and lets the replica retry, which is the honest
                // outcome until the promotion is made durably retryable (a transactional outbox for
                // detection state is follow-up work, not something this path can fake).
                Log.ConflictAppliedFlagFailed(_logger, conflictId, ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Derives, per conflict, the generation its own edit produced: the highest change generation this
    /// batch recorded for that specific feature. Conflicts whose edit was withheld (manual review) or
    /// never committed fall back to the pre-batch watermark, which is correct — nothing of theirs
    /// landed, so any later change to the feature is genuinely post-conflict.
    /// </summary>
    /// <remarks>
    /// Deliberately object-scoped rather than a global post-batch watermark: sampling the watermark
    /// after the batch can capture a concurrent edit to the same feature and bake it into the conflict
    /// snapshot, which would let a later resolution overwrite that edit (#2430). Scoping the probe to
    /// the conflicting object ids means unrelated churn on the layer cannot move any conflict's base.
    /// <para>
    /// The per-object result is nonetheless clamped to <paramref name="postBatchGeneration"/>, the
    /// watermark taken immediately after the batch committed. The change feed collapses to the latest
    /// change per object, so without the clamp a foreign edit to the same feature arriving between the
    /// commit and this probe would be read as the generation our own edit produced, and a staleness
    /// check starting after it would never see it. The clamp can only lower a base, never raise it past
    /// our own committed edit, so it cannot mask a genuine post-conflict edit.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, long>> ResolveConflictBaseGenerationsAsync(
        ReplicaUploadLayerEdits layer,
        ImmutableArray<ReplicaSyncConflict>.Builder conflicts,
        List<int> layerConflictIndexes,
        long preBatchGeneration,
        long postBatchGeneration,
        CancellationToken cancellationToken)
    {
        var byObjectId = layerConflictIndexes
            .Select(index => conflicts[index])
            .Where(conflict => conflict.ConflictId is { Length: > 0 })
            .GroupBy(conflict => conflict.ObjectId)
            .ToDictionary(group => group.Key, group => group.Select(conflict => conflict.ConflictId!).ToArray());

        var generations = new Dictionary<string, long>(StringComparer.Ordinal);
        if (byObjectId.Count == 0)
        {
            return generations;
        }

        var changes = await _changeTracker
            .GetChangesSinceAsync(preBatchGeneration, [layer.StorageLayerId], new HashSet<long>(byObjectId.Keys), cancellationToken)
            .ConfigureAwait(false);

        // Clamped to the post-batch watermark: the change feed collapses to the latest change per
        // object, so a foreign edit that landed after this batch committed would otherwise become the
        // conflict's base and hide itself from every later staleness check.
        var candidates = changes
            .Where(change => byObjectId.ContainsKey(change.ObjectId))
            .SelectMany(change => byObjectId[change.ObjectId]
                .Select(conflictId => (conflictId, Generation: Math.Min(change.Generation, postBatchGeneration))))
            .Where(candidate => !generations.TryGetValue(candidate.conflictId, out var current)
                || candidate.Generation > current);

        foreach (var (conflictId, generation) in candidates)
        {
            generations[conflictId] = generation;
        }

        return generations;
    }

    // mustRecord: manual review skips the conflicting edit, so the record is the only carrier of the
    // client intent and a failed write has to surface rather than silently drop the edit. Under
    // last-write-wins the edit is applied regardless, so a record failure is tolerable.
    //
    // The record is always written with ClientEditApplied=false; it is promoted by
    // MarkConflictsAppliedAsync once the layer's batch is known to have committed, so the flag
    // describes what actually landed rather than the requested conflict policy (#2430).
    private async Task<string?> RecordConflictAsync(
        ReplicaSyncRequest request,
        ReplicaUploadLayerEdits layer,
        long objectId,
        ReplicaConflictType conflictType,
        bool mustRecord,
        CancellationToken cancellationToken)
    {
        var publicLayerId = layer.PublicLayerId;
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
            ClientEditApplied = false,
            // Persisted so a later resolution can probe the change log for post-conflict edits without
            // re-resolving metadata (#2430).
            StorageLayerId = layer.StorageLayerId,
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

            // Manual review: the conflicting edit will be skipped, so the conflict record is the
            // only carrier of the client intent. Losing it would drop the edit with no trace, so
            // propagate the failure and fail the sync instead of returning null.
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

        [LoggerMessage(EventId = 7742, Level = LogLevel.Warning,
            Message = "Failed to mark replica conflict {ConflictId} as client-edit-applied after its layer batch committed; it stays recorded as not applied")]
        public static partial void ConflictAppliedFlagFailed(ILogger logger, string conflictId, Exception exception);
    }
}
