// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

public static class ReplicaSecurity
{
    public static bool CanAccess(string? ownerId, string? principalId, bool isAdmin)
        => isAdmin ||
           ownerId is not null &&
           string.Equals(ownerId, principalId, StringComparison.OrdinalIgnoreCase);

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
