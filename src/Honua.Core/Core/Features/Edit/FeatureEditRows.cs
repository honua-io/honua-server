// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Edit;

// Index counts rows of the same kind, matching the per-kind result arrays of FeatureEditResult.
public readonly record struct FeatureEditRow(FeatureEditOperationKind Kind, int Index, Feature? Feature, long? ObjectId);

public static class FeatureEditRows
{
    // Ordered operations win over the split lists, as they do for the writer.
    public static IReadOnlyList<FeatureEditRow> Enumerate(FeatureEditBatch batch)
    {
        var rows = new List<FeatureEditRow>(batch.TotalOperations);
        var counts = new int[3];
        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                var objectId = operation.Kind == FeatureEditOperationKind.Create
                    ? null
                    : operation.ObjectId ?? operation.Feature?.Id;
                rows.Add(Row(operation.Kind, counts, operation.Feature, objectId));
            }

            return rows;
        }

        AddFeatures(rows, counts, FeatureEditOperationKind.Create, batch.Creates);
        AddFeatures(rows, counts, FeatureEditOperationKind.Update, batch.Updates);
        foreach (var objectId in batch.Deletes.IsDefault ? ImmutableArray<long>.Empty : batch.Deletes)
        {
            rows.Add(Row(FeatureEditOperationKind.Delete, counts, feature: null, objectId));
        }

        return rows;
    }

    public static EditOperationResult? ResultOf(FeatureEditResult result, FeatureEditRow row)
    {
        var results = row.Kind switch
        {
            FeatureEditOperationKind.Create => result.CreateResults,
            FeatureEditOperationKind.Update => result.UpdateResults,
            _ => result.DeleteResults,
        };
        return !results.IsDefault && row.Index < results.Length ? results[row.Index] : null;
    }

    public static bool IsCommitted(FeatureEditResult result, FeatureEditRow row)
        => !result.WasRolledBack && ResultOf(result, row) is { IsSuccess: true };

    private static void AddFeatures(
        List<FeatureEditRow> rows,
        int[] counts,
        FeatureEditOperationKind kind,
        ImmutableArray<Feature> features)
    {
        foreach (var feature in features.IsDefault ? ImmutableArray<Feature>.Empty : features)
        {
            rows.Add(Row(kind, counts, feature, kind == FeatureEditOperationKind.Create ? null : feature.Id));
        }
    }

    private static FeatureEditRow Row(FeatureEditOperationKind kind, int[] counts, Feature? feature, long? objectId)
        => new(kind, counts[(int)kind]++, feature, objectId);
}
