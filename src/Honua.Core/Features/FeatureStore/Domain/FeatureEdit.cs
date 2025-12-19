// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a batch of feature edits to apply
/// </summary>
public readonly record struct FeatureEditBatch
{
    /// <summary>
    /// Features to create
    /// </summary>
    public ImmutableArray<Feature> Creates { get; init; }

    /// <summary>
    /// Features to update (must include Id)
    /// </summary>
    public ImmutableArray<Feature> Updates { get; init; }

    /// <summary>
    /// Feature IDs to delete
    /// </summary>
    public ImmutableArray<long> Deletes { get; init; }

    /// <summary>
    /// Creates a batch edit request
    /// </summary>
    /// <param name="creates">Features to create</param>
    /// <param name="updates">Features to update</param>
    /// <param name="deletes">Feature IDs to delete</param>
    /// <returns>Edit batch instance</returns>
    public static FeatureEditBatch Create(
        ImmutableArray<Feature> creates = default,
        ImmutableArray<Feature> updates = default,
        ImmutableArray<long> deletes = default)
        => new()
        {
            Creates = creates.IsDefault ? ImmutableArray<Feature>.Empty : creates,
            Updates = updates.IsDefault ? ImmutableArray<Feature>.Empty : updates,
            Deletes = deletes.IsDefault ? ImmutableArray<long>.Empty : deletes
        };

    /// <summary>
    /// Gets the total number of operations in this batch
    /// </summary>
    public int TotalOperations => Creates.Length + Updates.Length + Deletes.Length;

    /// <summary>
    /// Gets whether this batch contains any operations
    /// </summary>
    public bool IsEmpty => TotalOperations == 0;
}

/// <summary>
/// Result of applying feature edits
/// </summary>
public readonly record struct FeatureEditResult
{
    /// <summary>
    /// Number of features successfully created
    /// </summary>
    public required int CreatedCount { get; init; }

    /// <summary>
    /// Number of features successfully updated
    /// </summary>
    public required int UpdatedCount { get; init; }

    /// <summary>
    /// Number of features successfully deleted
    /// </summary>
    public required int DeletedCount { get; init; }

    /// <summary>
    /// IDs of newly created features
    /// </summary>
    public ImmutableArray<long> CreatedIds { get; init; }

    /// <summary>
    /// Errors that occurred during the operation
    /// </summary>
    public ImmutableArray<string> Errors { get; init; }

    /// <summary>
    /// Creates a successful edit result
    /// </summary>
    /// <param name="createdCount">Number of features created</param>
    /// <param name="updatedCount">Number of features updated</param>
    /// <param name="deletedCount">Number of features deleted</param>
    /// <param name="createdIds">IDs of newly created features</param>
    /// <returns>Edit result instance</returns>
    public static FeatureEditResult Success(
        int createdCount,
        int updatedCount,
        int deletedCount,
        ImmutableArray<long> createdIds = default)
        => new()
        {
            CreatedCount = createdCount,
            UpdatedCount = updatedCount,
            DeletedCount = deletedCount,
            CreatedIds = createdIds.IsDefault ? ImmutableArray<long>.Empty : createdIds,
            Errors = ImmutableArray<string>.Empty
        };

    /// <summary>
    /// Creates a failed edit result
    /// </summary>
    /// <param name="errors">Errors that occurred</param>
    /// <returns>Edit result instance</returns>
    public static FeatureEditResult Failure(params string[] errors)
        => new()
        {
            CreatedCount = 0,
            UpdatedCount = 0,
            DeletedCount = 0,
            CreatedIds = ImmutableArray<long>.Empty,
            Errors = errors.ToImmutableArray()
        };

    /// <summary>
    /// Gets whether the operation was successful
    /// </summary>
    public bool IsSuccess => Errors.IsEmpty;
}
