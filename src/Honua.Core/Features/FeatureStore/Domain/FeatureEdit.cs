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
    /// Whether to rollback all changes on failure
    /// Default is false (Esri default behavior)
    /// </summary>
    public bool RollbackOnFailure { get; init; } = false;

    /// <summary>
    /// Whether to use global IDs
    /// </summary>
    public bool UseGlobalIds { get; init; } = false;

    /// <summary>
    /// Initializes a new FeatureEditBatch with default values
    /// </summary>
    public FeatureEditBatch()
    {
    }

    /// <summary>
    /// Creates a batch edit request
    /// </summary>
    /// <param name="creates">Features to create</param>
    /// <param name="updates">Features to update</param>
    /// <param name="deletes">Feature IDs to delete</param>
    /// <param name="rollbackOnFailure">Whether to rollback all changes on failure</param>
    /// <param name="useGlobalIds">Whether to use global IDs</param>
    /// <returns>Edit batch instance</returns>
    public static FeatureEditBatch Create(
        ImmutableArray<Feature> creates = default,
        ImmutableArray<Feature> updates = default,
        ImmutableArray<long> deletes = default,
        bool rollbackOnFailure = false,
        bool useGlobalIds = false)
        => new()
        {
            Creates = creates.IsDefault ? ImmutableArray<Feature>.Empty : creates,
            Updates = updates.IsDefault ? ImmutableArray<Feature>.Empty : updates,
            Deletes = deletes.IsDefault ? ImmutableArray<long>.Empty : deletes,
            RollbackOnFailure = rollbackOnFailure,
            UseGlobalIds = useGlobalIds
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
    /// Results of individual create operations
    /// </summary>
    public ImmutableArray<EditOperationResult> CreateResults { get; init; }

    /// <summary>
    /// Results of individual update operations
    /// </summary>
    public ImmutableArray<EditOperationResult> UpdateResults { get; init; }

    /// <summary>
    /// Results of individual delete operations
    /// </summary>
    public ImmutableArray<EditOperationResult> DeleteResults { get; init; }

    /// <summary>
    /// Whether entire transaction was rolled back
    /// </summary>
    public bool WasRolledBack { get; init; }

    /// <summary>
    /// Creates a successful edit result
    /// </summary>
    /// <param name="createdCount">Number of features created</param>
    /// <param name="updatedCount">Number of features updated</param>
    /// <param name="deletedCount">Number of features deleted</param>
    /// <param name="createdIds">IDs of newly created features</param>
    /// <param name="createResults">Results of create operations</param>
    /// <param name="updateResults">Results of update operations</param>
    /// <param name="deleteResults">Results of delete operations</param>
    /// <param name="wasRolledBack">Whether transaction was rolled back</param>
    /// <returns>Edit result instance</returns>
    public static FeatureEditResult Success(
        int createdCount,
        int updatedCount,
        int deletedCount,
        ImmutableArray<long> createdIds = default,
        ImmutableArray<EditOperationResult> createResults = default,
        ImmutableArray<EditOperationResult> updateResults = default,
        ImmutableArray<EditOperationResult> deleteResults = default,
        bool wasRolledBack = false)
        => new()
        {
            CreatedCount = createdCount,
            UpdatedCount = updatedCount,
            DeletedCount = deletedCount,
            CreatedIds = createdIds.IsDefault ? ImmutableArray<long>.Empty : createdIds,
            CreateResults = createResults.IsDefault ? ImmutableArray<EditOperationResult>.Empty : createResults,
            UpdateResults = updateResults.IsDefault ? ImmutableArray<EditOperationResult>.Empty : updateResults,
            DeleteResults = deleteResults.IsDefault ? ImmutableArray<EditOperationResult>.Empty : deleteResults,
            WasRolledBack = wasRolledBack
        };

    /// <summary>
    /// Creates a failed edit result (all operations rolled back)
    /// </summary>
    /// <param name="createResults">Results of create operations</param>
    /// <param name="updateResults">Results of update operations</param>
    /// <param name="deleteResults">Results of delete operations</param>
    /// <returns>Edit result instance</returns>
    public static FeatureEditResult Rollback(
        ImmutableArray<EditOperationResult> createResults = default,
        ImmutableArray<EditOperationResult> updateResults = default,
        ImmutableArray<EditOperationResult> deleteResults = default)
        => new()
        {
            CreatedCount = 0,
            UpdatedCount = 0,
            DeletedCount = 0,
            CreatedIds = ImmutableArray<long>.Empty,
            CreateResults = createResults.IsDefault ? ImmutableArray<EditOperationResult>.Empty : createResults,
            UpdateResults = updateResults.IsDefault ? ImmutableArray<EditOperationResult>.Empty : updateResults,
            DeleteResults = deleteResults.IsDefault ? ImmutableArray<EditOperationResult>.Empty : deleteResults,
            WasRolledBack = true
        };

    /// <summary>
    /// Gets whether the operation was fully successful
    /// </summary>
    public bool IsSuccess => !WasRolledBack && HasErrors == false;

    /// <summary>
    /// Gets whether any individual operations had errors
    /// </summary>
    public bool HasErrors =>
        CreateResults.Any(r => !r.IsSuccess) ||
        UpdateResults.Any(r => !r.IsSuccess) ||
        DeleteResults.Any(r => !r.IsSuccess);
}

/// <summary>
/// Result of an individual edit operation
/// </summary>
public readonly record struct EditOperationResult
{
    /// <summary>
    /// Object ID of the affected feature
    /// </summary>
    public long? ObjectId { get; init; }

    /// <summary>
    /// Global ID of the affected feature (if applicable)
    /// </summary>
    public string? GlobalId { get; init; }

    /// <summary>
    /// Whether this operation succeeded
    /// </summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Error message if operation failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Error code if operation failed
    /// </summary>
    public int ErrorCode { get; init; }

    /// <summary>
    /// Initializes a new EditOperationResult with default values
    /// </summary>
    public EditOperationResult()
    {
    }

    /// <summary>
    /// Creates a successful operation result
    /// </summary>
    /// <param name="objectId">Object ID of the feature</param>
    /// <param name="globalId">Global ID of the feature</param>
    /// <returns>Operation result</returns>
    public static EditOperationResult Success(long objectId, string? globalId = null)
        => new()
        {
            ObjectId = objectId,
            GlobalId = globalId,
            IsSuccess = true
        };

    /// <summary>
    /// Creates a failed operation result
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <param name="errorCode">Error code</param>
    /// <param name="objectId">Object ID if known</param>
    /// <returns>Operation result</returns>
    public static EditOperationResult Failure(string errorMessage, int errorCode = 1000, long? objectId = null)
        => new()
        {
            ObjectId = objectId,
            IsSuccess = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode
        };
}
