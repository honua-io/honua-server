// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Slices 2-5 of the temporal history service (honua-server#1166): diff, feature timeline, attribution,
/// and governed rollback planning/execution.
/// </summary>
/// <remarks>
/// <para>
/// All four surfaces build on the same change-log foundation as slice 1. The DEFAULT change log records
/// only object identity, net operation, generation, timestamp, and (after the slice-4 migration)
/// attribution — it does NOT retain historical attribute or geometry snapshots. The diff and timeline
/// therefore classify changes by the recorded operation and surface the CURRENT attribute snapshot for
/// surviving features (masked per policy); prior-snapshot values are unavailable, consistent with the
/// slice-1 as-of read's documented limitation. The contract carries old/new field slots and a geometry
/// indicator so richer detail can be filled in once snapshot storage exists.
/// </para>
/// <para>
/// Governed rollback never deletes history: <see cref="ExecuteRollbackAsync"/> submits a NEW forward
/// corrective operation through the job runner (<see cref="ITemporalRollbackRunner"/>) which re-applies the
/// target state via the canonical edit pipeline, recording new change-log rows and advancing the
/// generation — producing a new checkpoint while leaving the immutable audit history intact.
/// </para>
/// </remarks>
public sealed partial class TemporalHistoryService
{
    /// <inheritdoc />
    public async Task<TemporalDiffResult> DiffAsync(
        string serviceId,
        int layerId,
        TemporalDiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveHistoryLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var storageLayerId = resolved.StorageLayerId!.Value;

        var currentGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);

        var from = await _checkpointResolver
            .ResolveAsync(storageLayerId, request.From, currentGeneration, cancellationToken)
            .ConfigureAwait(false);
        var to = await _checkpointResolver
            .ResolveAsync(
                storageLayerId,
                request.To ?? TemporalCheckpoint.ForGeneration(currentGeneration),
                currentGeneration,
                cancellationToken)
            .ConfigureAwait(false);

        if (to.Generation < from.Generation)
        {
            throw new TemporalValidationException(
                "The target checkpoint must be at or after the base checkpoint.");
        }

        var limit = NormalizeLimit(request.Limit);
        var afterObjectId = DecodeObjectIdCursor(request.Cursor);

        // Summary counts cover the full diff window; the page is bounded by limit.
        var totalChanged = await _historyStore
            .CountChangedFeaturesAsync(storageLayerId, from.Generation, to.Generation, cancellationToken)
            .ConfigureAwait(false);

        // Read one extra row group beyond the limit to determine whether another page exists. The store
        // orders by object id then generation, so we collapse per object id into a net operation here.
        var rows = await _historyStore
            .GetChangesInWindowAsync(storageLayerId, from.Generation, to.Generation, afterObjectId, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var grouped = CollapseByObjectId(rows);

        var hasMore = grouped.Count > limit;
        var pageGroups = hasMore ? grouped.Take(limit).ToList() : grouped;

        var changes = ImmutableArray.CreateBuilder<TemporalFeatureDiff>(pageGroups.Count);
        int added = 0, removed = 0, attributeChanged = 0, geometryChanged = 0;

        foreach (var group in pageGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diff = await BuildFeatureDiffAsync(serviceId, layerId, storageLayerId, group, cancellationToken)
                .ConfigureAwait(false);
            changes.Add(diff);
        }

        // Summary counts are derived from the full window, not just the page. Recompute per-class totals
        // from the store using lightweight passes would require extra round-trips; instead we report the
        // page-accurate class split plus the authoritative total of distinct changed features.
        foreach (var group in grouped)
        {
            switch (group.NetOperation)
            {
                case TemporalChangeKind.Insert: added++; break;
                case TemporalChangeKind.Delete: removed++; break;
                default:
                    attributeChanged++;
                    if (group.GeometryChanged)
                    {
                        geometryChanged++;
                    }

                    break;
            }
        }

        var summary = new TemporalDiffSummary(
            Added: added,
            Removed: removed,
            AttributeChanged: attributeChanged,
            GeometryChanged: geometryChanged,
            Total: totalChanged);

        var nextCursor = hasMore && pageGroups.Count > 0
            ? EncodeObjectIdCursor(pageGroups[^1].ObjectId)
            : null;

        return new TemporalDiffResult(
            ServiceId: serviceId,
            LayerId: layerId,
            FromCheckpoint: from,
            ToCheckpoint: to,
            Summary: summary,
            Changes: changes.ToImmutable(),
            NextCursor: nextCursor);
    }

    /// <inheritdoc />
    public async Task<TemporalTimeline> GetTimelineAsync(
        string serviceId,
        int layerId,
        TemporalTimelineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ObjectId < 0)
        {
            throw new TemporalValidationException("Object id must be non-negative.");
        }

        var resolved = await ResolveHistoryLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var storageLayerId = resolved.StorageLayerId!.Value;

        var currentGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        var limit = NormalizeLimit(request.Limit);
        var afterGeneration = DecodeGenerationCursor(request.Cursor);

        var rows = await _historyStore
            .GetFeatureRevisionsAsync(storageLayerId, request.ObjectId, afterGeneration, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows.ToList();

        var revisions = ImmutableArray.CreateBuilder<TemporalRevision>(pageRows.Count);
        foreach (var row in pageRows)
        {
            revisions.Add(new TemporalRevision(
                Generation: row.Generation,
                Operation: row.Operation,
                ChangedAtUtc: row.ChangedAtUtc.ToUniversalTime(),
                Attribution: row.Attribution));
        }

        var nextCursor = hasMore && pageRows.Count > 0
            ? EncodeGenerationCursor(pageRows[^1].Generation)
            : null;

        return new TemporalTimeline(
            ServiceId: serviceId,
            LayerId: layerId,
            ObjectId: request.ObjectId,
            CurrentGeneration: currentGeneration,
            Revisions: revisions.ToImmutable(),
            NextCursor: nextCursor);
    }

    /// <inheritdoc />
    public async Task<TemporalRollbackPlan> PlanRollbackAsync(
        string serviceId,
        int layerId,
        TemporalRollbackPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveHistoryLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var storageLayerId = resolved.StorageLayerId!.Value;

        var currentGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        var target = await _checkpointResolver
            .ResolveAsync(storageLayerId, request.TargetCheckpoint, currentGeneration, cancellationToken)
            .ConfigureAwait(false);

        return await BuildRollbackPlanAsync(serviceId, layerId, storageLayerId, target, currentGeneration, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemporalRollbackJobHandle> ExecuteRollbackAsync(
        string serviceId,
        int layerId,
        TemporalRollbackExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveHistoryLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var storageLayerId = resolved.StorageLayerId!.Value;

        var currentGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        var target = await _checkpointResolver
            .ResolveAsync(storageLayerId, request.TargetCheckpoint, currentGeneration, cancellationToken)
            .ConfigureAwait(false);

        var plan = await BuildRollbackPlanAsync(serviceId, layerId, storageLayerId, target, currentGeneration, cancellationToken)
            .ConfigureAwait(false);

        if (plan.State != TemporalRollbackState.Supported)
        {
            throw new TemporalRollbackNotPermittedException(
                $"Rollback for layer '{layerId}' is not runnable automatically (state: {plan.State}).");
        }

        if (plan.RequiresApproval && !request.Approved)
        {
            throw new TemporalRollbackNotPermittedException(
                "This rollback requires explicit approval; resubmit with approval granted.");
        }

        return await _rollbackRunner
            .SubmitRollbackAsync(serviceId, layerId, storageLayerId, target, request.Reason, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TemporalRollbackPlan> BuildRollbackPlanAsync(
        string serviceId,
        int layerId,
        int storageLayerId,
        ResolvedTemporalCheckpoint target,
        long currentGeneration,
        CancellationToken cancellationToken)
    {
        // The corrective operation re-applies the layer's state at the target checkpoint. The number of
        // features changed between the target and the current generation is the count the corrective job
        // must touch to restore that state.
        var affected = await _historyStore
            .CountChangedFeaturesAsync(storageLayerId, target.Generation, currentGeneration, cancellationToken)
            .ConfigureAwait(false);

        var validation = ImmutableArray.CreateBuilder<TemporalRollbackFinding>();
        var compatibility = ImmutableArray.CreateBuilder<TemporalRollbackFinding>();

        var state = TemporalRollbackState.Supported;
        var requiresApproval = false;

        if (target.Generation >= currentGeneration)
        {
            // Nothing to roll back: the target is already the current state.
            validation.Add(new TemporalRollbackFinding(
                Code: "no-op",
                Severity: TemporalRollbackFindingSeverity.Info,
                Message: "The target checkpoint is already the current state; the rollback is a no-op."));
        }
        else if (affected == 0)
        {
            validation.Add(new TemporalRollbackFinding(
                Code: "no-op",
                Severity: TemporalRollbackFindingSeverity.Info,
                Message: "No features changed since the target checkpoint; the rollback is a no-op."));
        }

        // The corrective path cannot reconstruct prior attribute/geometry snapshots from the DEFAULT
        // change log alone (it records identity and operation only). Inserts since the target can be
        // deleted and deletes can be re-created by id, but updates require the prior row image. We
        // surface this as a compatibility finding and require operator approval whenever updates are in
        // scope, classifying such rollbacks as Supported-with-approval (the corrective job restores what
        // it can and reports the rest), keeping the path honest about the foundation's limits.
        if (affected > 0 && target.Generation < currentGeneration)
        {
            compatibility.Add(new TemporalRollbackFinding(
                Code: "snapshot-unavailable",
                Severity: TemporalRollbackFindingSeverity.Warning,
                Message: "Prior attribute/geometry snapshots are not retained by the change log; the "
                    + "corrective operation restores feature presence and requires operator approval."));
            requiresApproval = true;
        }

        if (validation.Any(f => f.Severity == TemporalRollbackFindingSeverity.Error)
            || compatibility.Any(f => f.Severity == TemporalRollbackFindingSeverity.Error))
        {
            state = TemporalRollbackState.Blocked;
        }

        return new TemporalRollbackPlan(
            ServiceId: serviceId,
            LayerId: layerId,
            TargetCheckpoint: target,
            CurrentGeneration: currentGeneration,
            State: state,
            AffectedFeatureCount: affected,
            ValidationFindings: validation.ToImmutable(),
            CompatibilityFindings: compatibility.ToImmutable(),
            RequiresApproval: requiresApproval);
    }

    private async Task<TemporalFeatureDiff> BuildFeatureDiffAsync(
        string serviceId,
        int layerId,
        int storageLayerId,
        ObjectChangeGroup group,
        CancellationToken cancellationToken)
    {
        var classes = ImmutableArray.CreateBuilder<TemporalDiffChangeClass>();
        var fieldChanges = ImmutableArray<TemporalFieldChange>.Empty;
        TemporalDiffChangeClass primary;
        var geometryChanged = group.GeometryChanged;

        switch (group.NetOperation)
        {
            case TemporalChangeKind.Insert:
                primary = TemporalDiffChangeClass.Added;
                classes.Add(TemporalDiffChangeClass.Added);
                fieldChanges = await BuildCurrentSnapshotFieldChangesAsync(
                    serviceId, layerId, storageLayerId, group.ObjectId, isAdd: true, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case TemporalChangeKind.Delete:
                primary = TemporalDiffChangeClass.Removed;
                classes.Add(TemporalDiffChangeClass.Removed);
                break;
            default:
                primary = TemporalDiffChangeClass.AttributeChanged;
                classes.Add(TemporalDiffChangeClass.AttributeChanged);
                if (geometryChanged)
                {
                    classes.Add(TemporalDiffChangeClass.GeometryChanged);
                }

                fieldChanges = await BuildCurrentSnapshotFieldChangesAsync(
                    serviceId, layerId, storageLayerId, group.ObjectId, isAdd: false, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }

        return new TemporalFeatureDiff(
            ObjectId: group.ObjectId,
            PrimaryClass: primary,
            Classes: classes.ToImmutable(),
            GeometryChanged: geometryChanged,
            FieldChanges: fieldChanges,
            Attribution: group.Attribution);
    }

    // Surfaces the CURRENT attribute snapshot as the "new" side of a diff for surviving features, masked
    // per policy. Prior-snapshot values are unavailable from the change log, so OldValue is null.
    private async Task<ImmutableArray<TemporalFieldChange>> BuildCurrentSnapshotFieldChangesAsync(
        string serviceId,
        int layerId,
        int storageLayerId,
        long objectId,
        bool isAdd,
        CancellationToken cancellationToken)
    {
        var feature = await _featureReader.GetAsync(storageLayerId, objectId, cancellationToken).ConfigureAwait(false);
        if (feature is not { } present)
        {
            return ImmutableArray<TemporalFieldChange>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<TemporalFieldChange>(present.Attributes.Count);
        foreach (var (field, value) in present.Attributes)
        {
            var masked = _maskingPolicy.IsFieldMasked(serviceId, layerId, field);
            builder.Add(new TemporalFieldChange(
                Field: field,
                OldValue: null,
                NewValue: masked ? null : value,
                Masked: masked));
        }

        _ = isAdd;
        return builder.ToImmutable();
    }

    private async Task<ResolvedLayer> ResolveHistoryLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);

        var temporalColumn = resolved.StorageMapping?.TemporalColumn;
        if (string.IsNullOrWhiteSpace(temporalColumn)
            || resolved.StorageLayerId is null
            || !IsHistoryTrackingTracker(_changeTracker))
        {
            throw new TemporalNotSupportedException(
                $"Layer '{resolved.Resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' "
                + "does not support temporal history reads.");
        }

        return resolved;
    }

    private static List<ObjectChangeGroup> CollapseByObjectId(IReadOnlyList<TemporalChangeRecord> rows)
    {
        var groups = new List<ObjectChangeGroup>();
        long? currentId = null;
        TemporalChangeKind firstOp = default;
        TemporalChangeKind lastOp = default;
        var updateCount = 0;
        TemporalAttribution? lastAttribution = null;

        void Flush()
        {
            if (currentId is null)
            {
                return;
            }

            var net = ResolveNetOperation(firstOp, lastOp);
            if (net is { } resolvedNet)
            {
                groups.Add(new ObjectChangeGroup(
                    ObjectId: currentId.Value,
                    NetOperation: resolvedNet,
                    GeometryChanged: false,
                    Attribution: lastAttribution));
            }
        }

        foreach (var row in rows)
        {
            if (currentId != row.ObjectId)
            {
                Flush();
                currentId = row.ObjectId;
                firstOp = row.Operation;
                updateCount = 0;
            }

            lastOp = row.Operation;
            lastAttribution = row.Attribution;
            if (row.Operation == TemporalChangeKind.Update)
            {
                updateCount++;
            }
        }

        Flush();
        _ = updateCount;
        return groups;
    }

    // Collapses a feature's first/last operations into a single net change, mirroring the replication
    // collapse: insert+...+delete = omitted; insert+... = insert; ...+delete = delete; else update.
    private static TemporalChangeKind? ResolveNetOperation(TemporalChangeKind first, TemporalChangeKind last)
    {
        if (first == TemporalChangeKind.Insert && last == TemporalChangeKind.Delete)
        {
            return null;
        }

        if (first == TemporalChangeKind.Insert)
        {
            return TemporalChangeKind.Insert;
        }

        if (last == TemporalChangeKind.Delete)
        {
            return TemporalChangeKind.Delete;
        }

        return TemporalChangeKind.Update;
    }

    private static int NormalizeLimit(int? requested)
    {
        if (requested is { } value)
        {
            if (value <= 0)
            {
                throw new TemporalValidationException("Limit must be greater than zero when provided.");
            }

            return Math.Min(value, MaxLimit);
        }

        return DefaultLimit;
    }

    private static long DecodeObjectIdCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0L;
        }

        if (!long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new TemporalValidationException("Invalid page cursor.");
        }

        return value;
    }

    private static string EncodeObjectIdCursor(long objectId)
        => objectId.ToString(CultureInfo.InvariantCulture);

    private static long DecodeGenerationCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0L;
        }

        if (!long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new TemporalValidationException("Invalid page cursor.");
        }

        return value;
    }

    private static string EncodeGenerationCursor(long generation)
        => generation.ToString(CultureInfo.InvariantCulture);

    private sealed record ObjectChangeGroup(
        long ObjectId,
        TemporalChangeKind NetOperation,
        bool GeometryChanged,
        TemporalAttribution? Attribution);
}
