// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Db.Postgres.Features.FeatureStore;

internal sealed partial class PostgresFeatureStoreRefactored
{
    private sealed class OptimizedQueryContext<T>
    {
        public required int LayerId { get; init; }
        public required FeatureQuery Query { get; init; }
        public required CoreGeometryStorageType GeometryStorageType { get; init; }
        public required Func<FeatureDataAccess, Task<(ImmutableArray<T> Items, long? TotalCount)>> ExecuteAsync { get; init; }
    }

    private sealed class StreamOptimizedFallbackContext<T>
    {
        public required int LayerId { get; init; }
        public required FeatureQuery Query { get; init; }
        public required CoreGeometryStorageType GeometryStorageType { get; init; }
        public required IAsyncEnumerable<T> Stream { get; init; }
        public required Func<T, ImmutableDictionary<string, object?>> GetAttributes { get; init; }
        public required Func<T, T> RemoveInternal { get; init; }
    }

    internal static FeatureEditBatch ApplyMaskedFieldsToEditBatch(
        FeatureEditBatch editBatch,
        ImmutableArray<string> maskedFields)
    {
        if (!editBatch.Preconditions.IsDefaultOrEmpty)
        {
            editBatch = editBatch with
            {
                Preconditions = editBatch.Preconditions
                    .Select(precondition => precondition with { MaskedFields = maskedFields })
                    .ToImmutableArray()
            };
        }

        if (maskedFields.IsDefaultOrEmpty)
        {
            return editBatch;
        }

        if (!editBatch.Updates.IsDefaultOrEmpty)
        {
            editBatch = editBatch with
            {
                Updates = editBatch.Updates
                    .Select(feature => MergePreservedAttributeNames(feature, maskedFields))
                    .ToImmutableArray()
            };
        }

        if (!editBatch.Operations.IsDefaultOrEmpty)
        {
            editBatch = ApplyMaskedFieldsToOperations(editBatch, maskedFields);
        }

        return editBatch;
    }

    private static FeatureEditBatch ApplyMaskedFieldsToOperations(
        FeatureEditBatch editBatch,
        ImmutableArray<string> maskedFields)
    {
        var updatedOperations = ImmutableArray.CreateBuilder<FeatureEditOperation>(editBatch.Operations.Length);
        var changed = false;
        foreach (var operation in editBatch.Operations)
        {
            if (operation.Kind == FeatureEditOperationKind.Update && operation.Feature.HasValue)
            {
                updatedOperations.Add(operation with
                {
                    Feature = MergePreservedAttributeNames(operation.Feature.Value, maskedFields)
                });
                changed = true;
            }
            else
            {
                updatedOperations.Add(operation);
            }
        }

        return changed
            ? editBatch with { Operations = updatedOperations.MoveToImmutable() }
            : editBatch;
    }

    private static Feature MergePreservedAttributeNames(Feature feature, ImmutableArray<string> maskedFields)
    {
        if (feature.PreservedAttributeNames.IsDefaultOrEmpty)
        {
            return feature with { PreservedAttributeNames = maskedFields };
        }

        var seen = new HashSet<string>(feature.PreservedAttributeNames, StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<string>(feature.PreservedAttributeNames.Length + maskedFields.Length);
        builder.AddRange(feature.PreservedAttributeNames);
        foreach (var maskedField in maskedFields)
        {
            if (seen.Add(maskedField))
            {
                builder.Add(maskedField);
            }
        }

        return feature with { PreservedAttributeNames = builder.ToImmutable() };
    }

    private async Task<QueryResult<T>> ExecutePostgresOptimizedQueryAsync<T>(
        FeatureDataAccess postgresDataAccess,
        OptimizedQueryContext<T> context,
        CancellationToken cancellationToken)
    {
        var (items, windowTotal) = await context.ExecuteAsync(postgresDataAccess).ConfigureAwait(false);
        if (items.Length == 0)
        {
            return await CountEmptyPageAsync<T>(
                context.LayerId, context.Query, context.GeometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        var total = windowTotal ?? items.Length;
        var pageOffset = context.Query.Offset ?? 0;
        return QueryResult<T>.Create(total, items, total > pageOffset + items.Length);
    }

    private async Task<QueryResult<T>> ExecuteStreamOptimizedFallbackAsync<T>(
        StreamOptimizedFallbackContext<T> context,
        CancellationToken cancellationToken)
    {
        var capacity = context.Query.Limit is > 0 and < int.MaxValue ? Math.Min(context.Query.Limit.Value, 1024) : 256;
        var features = ImmutableArray.CreateBuilder<T>(capacity);
        long totalCount = 0;
        var hasCountColumn = false;

        await foreach (var feature in context.Stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!hasCountColumn &&
                context.GetAttributes(feature).TryGetValue(FeatureQueryEncoding.InternalTotalCountColumn, out var totalCountValue) &&
                totalCountValue is not null)
            {
                totalCount = Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
                hasCountColumn = true;
            }

            features.Add(context.RemoveInternal(feature));
        }

        if (!hasCountColumn)
        {
            totalCount = features.Count;
        }

        if (features.Count == 0)
        {
            return await CountEmptyPageAsync<T>(
                context.LayerId, context.Query, context.GeometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        var offset = context.Query.Offset ?? 0;
        var hasMoreResults = totalCount > offset + features.Count;
        return QueryResult<T>.Create(totalCount, features.DrainToImmutable(), hasMoreResults);
    }
}
