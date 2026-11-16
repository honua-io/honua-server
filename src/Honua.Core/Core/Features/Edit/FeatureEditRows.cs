// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Edit;

// Index counts rows of the same kind, matching the per-kind result arrays of FeatureEditResult.
public readonly record struct FeatureEditRow(FeatureEditOperationKind Kind, int Index)
{
    public Feature? Feature { get; init; }

    public long? ObjectId { get; init; }
}

public static class FeatureEditRows
{
    // Ordered operations win over the split lists, as they do for the writer.
    public static IReadOnlyList<FeatureEditRow> Enumerate(FeatureEditBatch batch)
    {
        var rows = new RowList(batch.TotalOperations);
        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                var objectId = operation.Kind == FeatureEditOperationKind.Create
                    ? null
                    : operation.ObjectId ?? operation.Feature?.Id;
                rows.Add(operation.Kind, operation.Feature, objectId);
            }

            return rows.Rows;
        }

        rows.AddFeatures(FeatureEditOperationKind.Create, batch.Creates);
        rows.AddFeatures(FeatureEditOperationKind.Update, batch.Updates);
        foreach (var objectId in batch.Deletes.IsDefault ? ImmutableArray<long>.Empty : batch.Deletes)
        {
            rows.Add(FeatureEditOperationKind.Delete, feature: null, objectId);
        }

        return rows.Rows;
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

    // Numbers each row within its kind as it is added.
    private sealed class RowList(int capacity)
    {
        private readonly int[] _counts = new int[3];

        public List<FeatureEditRow> Rows { get; } = new(capacity);

        public void Add(FeatureEditOperationKind kind, Feature? feature, long? objectId)
            => Rows.Add(new FeatureEditRow(kind, _counts[(int)kind]++) { Feature = feature, ObjectId = objectId });

        public void AddFeatures(FeatureEditOperationKind kind, ImmutableArray<Feature> features)
        {
            foreach (var feature in features.IsDefault ? ImmutableArray<Feature>.Empty : features)
            {
                Add(kind, feature, kind == FeatureEditOperationKind.Create ? null : feature.Id);
            }
        }
    }
}
