// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Represents a batch of feature edits.
/// </summary>
public sealed record FeatureEditBatch
{
    /// <summary>
    /// Features to create (without IDs).
    /// </summary>
    public IReadOnlyList<Feature> Creates { get; init; } = Array.Empty<Feature>();

    /// <summary>
    /// Features to update (must include IDs).
    /// </summary>
    public IReadOnlyList<Feature> Updates { get; init; } = Array.Empty<Feature>();

    /// <summary>
    /// Object IDs of features to delete.
    /// </summary>
    public IReadOnlyList<long> Deletes { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Whether to rollback all edits if any operation fails.
    /// </summary>
    public bool RollbackOnFailure { get; init; } = true;

    /// <summary>
    /// Whether to force write operations (bypass some validations).
    /// </summary>
    public bool ForceWrite { get; init; }

    /// <summary>
    /// Creates a batch with only create operations.
    /// </summary>
    public static FeatureEditBatch CreateOnly(IReadOnlyList<Feature> features, bool rollbackOnFailure = true)
    {
        return new FeatureEditBatch
        {
            Creates = features,
            RollbackOnFailure = rollbackOnFailure
        };
    }

    /// <summary>
    /// Creates a batch with only update operations.
    /// </summary>
    public static FeatureEditBatch UpdateOnly(IReadOnlyList<Feature> features, bool rollbackOnFailure = true)
    {
        return new FeatureEditBatch
        {
            Updates = features,
            RollbackOnFailure = rollbackOnFailure
        };
    }

    /// <summary>
    /// Creates a batch with only delete operations.
    /// </summary>
    public static FeatureEditBatch DeleteOnly(IReadOnlyList<long> objectIds, bool rollbackOnFailure = true)
    {
        return new FeatureEditBatch
        {
            Deletes = objectIds,
            RollbackOnFailure = rollbackOnFailure
        };
    }
}

/// <summary>
/// Represents the result of edit operations.
/// </summary>
public sealed record EditResult
{
    /// <summary>
    /// Results of create operations.
    /// </summary>
    public IReadOnlyList<OperationResult> CreateResults { get; init; } = Array.Empty<OperationResult>();

    /// <summary>
    /// Results of update operations.
    /// </summary>
    public IReadOnlyList<OperationResult> UpdateResults { get; init; } = Array.Empty<OperationResult>();

    /// <summary>
    /// Results of delete operations.
    /// </summary>
    public IReadOnlyList<OperationResult> DeleteResults { get; init; } = Array.Empty<OperationResult>();

    /// <summary>
    /// Global error if the entire operation failed.
    /// </summary>
    public EditError? Error { get; init; }

    /// <summary>
    /// Whether all operations were successful.
    /// </summary>
    public bool IsSuccess => Error == null &&
        CreateResults.All(r => r.Success) &&
        UpdateResults.All(r => r.Success) &&
        DeleteResults.All(r => r.Success);
}

/// <summary>
/// Represents the result of a single edit operation.
/// </summary>
public sealed record OperationResult
{
    /// <summary>
    /// The object ID of the affected feature.
    /// </summary>
    public long ObjectId { get; init; }

    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error details if the operation failed.
    /// </summary>
    public EditError? Error { get; init; }
}

/// <summary>
/// Represents an error that occurred during an edit operation.
/// </summary>
public sealed record EditError
{
    /// <summary>
    /// Error code.
    /// </summary>
    public int Code { get; init; }

    /// <summary>
    /// Error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Statistical operations that can be performed on queries.
/// </summary>
public sealed record StatisticDefinition
{
    /// <summary>
    /// The field to calculate statistics for.
    /// </summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>
    /// The type of statistic to calculate.
    /// </summary>
    public StatisticType Type { get; init; }

    /// <summary>
    /// The name of the output field for the statistic.
    /// </summary>
    public string OutputFieldName { get; init; } = string.Empty;
}

/// <summary>
/// Types of statistics that can be calculated.
/// </summary>
public enum StatisticType
{
    Count,
    Sum,
    Min,
    Max,
    Average,
    StandardDeviation,
    Variance
}