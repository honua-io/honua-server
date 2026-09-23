// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Shared authorization and visibility rules for the Esri replica surfaces.
/// </summary>
public static class ReplicaSecurity
{
    /// <summary>
    /// Returns whether a caller may see or mutate a replica registration.
    /// Legacy registrations without an owner are intentionally admin-only.
    /// </summary>
    public static bool CanAccess(string? ownerId, string? principalId, bool isAdmin)
        => isAdmin ||
           ownerId is not null &&
           string.Equals(ownerId, principalId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Filters change-log IDs through the caller's current row-visibility view. Deletes are
    /// suppressed when a visibility policy is active because the deleted row is no longer
    /// available to authorize from the live table. Changes that carry a pre-change image are
    /// classified by <see cref="ClassifyWithPreChangeImage"/> instead, which re-authorises deletes.
    /// </summary>
    public static (long[] InsertIds, long[] UpdateIds, long[] DeleteIds) FilterChangeIds(
        IReadOnlyList<FeatureChange> changes,
        IReadOnlySet<long> visibleCurrentIds,
        bool suppressDeletes)
    {
        var insertIds = changes
            .Where(change => change.Operation == FeatureChangeOperation.Insert && visibleCurrentIds.Contains(change.ObjectId))
            .Select(change => change.ObjectId)
            .ToArray();
        var updateIds = changes
            .Where(change => change.Operation == FeatureChangeOperation.Update && visibleCurrentIds.Contains(change.ObjectId))
            .Select(change => change.ObjectId)
            .ToArray();
        var deleteIds = suppressDeletes
            ? []
            : changes
                .Where(change => change.Operation == FeatureChangeOperation.Delete)
                .Select(change => change.ObjectId)
                .ToArray();

        return (insertIds, updateIds, deleteIds);
    }

    /// <summary>
    /// Classifies collapsed updates and deletes whose state before the window is known from the change
    /// log's pre-change image (#4879). "Visible" means inside the replica scope and readable under the
    /// caller's current row visibility; the prior state is judged from the pre-change image, the current
    /// state from the live row.
    /// <list type="bullet">
    /// <item>update, visible before and now: update (the client holds the row);</item>
    /// <item>update, visible now only: insert (the row is new to the client);</item>
    /// <item>update, visible before only: delete (the client must drop the row);</item>
    /// <item>delete, visible before: delete;</item>
    /// <item>anything never visible: nothing, so no id the caller could not read is disclosed.</item>
    /// </list>
    /// Inserts and changes without a known prior state are ignored here; callers classify them from
    /// the current state only.
    /// </summary>
    /// <param name="changes">Collapsed changes of one layer.</param>
    /// <param name="visibleCurrentIds">Object ids whose live row is visible now.</param>
    /// <param name="visibleBeforeIds">Object ids whose pre-change image is visible.</param>
    /// <returns>The ids to deliver as inserts, updates and deletes.</returns>
    public static (long[] InsertIds, long[] UpdateIds, long[] DeleteIds) ClassifyWithPreChangeImage(
        IReadOnlyList<FeatureChange> changes,
        IReadOnlySet<long> visibleCurrentIds,
        IReadOnlySet<long> visibleBeforeIds)
    {
        var insertIds = new List<long>();
        var updateIds = new List<long>();
        var deleteIds = new List<long>();
        foreach (var change in changes)
        {
            if (change.PreImageChangeId is null || change.Operation == FeatureChangeOperation.Insert)
            {
                continue;
            }

            var wasVisible = visibleBeforeIds.Contains(change.ObjectId);
            var isVisible = change.Operation == FeatureChangeOperation.Update && visibleCurrentIds.Contains(change.ObjectId);
            if (wasVisible && isVisible)
            {
                updateIds.Add(change.ObjectId);
            }
            else if (isVisible)
            {
                insertIds.Add(change.ObjectId);
            }
            else if (wasVisible)
            {
                deleteIds.Add(change.ObjectId);
            }
        }

        return ([.. insertIds], [.. updateIds], [.. deleteIds]);
    }
}
