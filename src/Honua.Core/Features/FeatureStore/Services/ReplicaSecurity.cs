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
    /// available to authorize from the live table.
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
}
