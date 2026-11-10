// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FeatureStore.Domain;

// The owner value must match in the same write statement; it is not a client precondition.
public readonly record struct OwnerEditGuard(long ObjectId, string Field, string Expected)
{
    public static OwnerEditGuard? Find(ImmutableArray<OwnerEditGuard> guards, long objectId)
    {
        if (guards.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var guard in guards)
        {
            if (guard.ObjectId == objectId)
            {
                return guard;
            }
        }

        return null;
    }
}

public readonly record struct FeatureEditBatch
{
    public ImmutableArray<FeatureEditOperation> Operations { get; init; }

    public ImmutableArray<Feature> Creates { get; init; }

    public ImmutableArray<Feature> Updates { get; init; }

    public ImmutableArray<long> Deletes { get; init; }

    public bool RollbackOnFailure { get; init; } = false;

    public bool UseGlobalIds { get; init; } = false;

    // A null version is the default and must use the non-versioned write path.
    public VersionContext? VersionContext { get; init; }

    // A precondition is re-checked inside the write transaction, and a mismatch leaves that operation unapplied.
    public ImmutableArray<FeatureEditPrecondition> Preconditions { get; init; }

    // Protocol adapters never set this predicate; the store stamps it, and a row outside the caller's partition fails the write.
    [JsonIgnore]
    public Honua.Core.Queries.Filters.SqlFragment? RowSecurityWriteCheck { get; init; }

    // The guard is applied in the same statement so a concurrent ownership transfer is not overwritten.
    [JsonIgnore]
    public ImmutableArray<OwnerEditGuard> OwnerGuards { get; init; }

    public FeatureEditBatch()
    {
        Operations = ImmutableArray<FeatureEditOperation>.Empty;
        Creates = ImmutableArray<Feature>.Empty;
        Updates = ImmutableArray<Feature>.Empty;
        Deletes = ImmutableArray<long>.Empty;
        Preconditions = ImmutableArray<FeatureEditPrecondition>.Empty;
        OwnerGuards = ImmutableArray<OwnerEditGuard>.Empty;
    }

    public static FeatureEditBatch Create(
        ImmutableArray<Feature> creates = default,
        ImmutableArray<Feature> updates = default,
        ImmutableArray<long> deletes = default,
        bool rollbackOnFailure = false,
        bool useGlobalIds = false,
        ImmutableArray<FeatureEditOperation> operations = default,
        VersionContext? versionContext = null,
        ImmutableArray<FeatureEditPrecondition> preconditions = default)
        => new()
        {
            Operations = operations.IsDefault ? ImmutableArray<FeatureEditOperation>.Empty : operations,
            Creates = creates.IsDefault ? ImmutableArray<Feature>.Empty : creates,
            Updates = updates.IsDefault ? ImmutableArray<Feature>.Empty : updates,
            Deletes = deletes.IsDefault ? ImmutableArray<long>.Empty : deletes,
            RollbackOnFailure = rollbackOnFailure,
            UseGlobalIds = useGlobalIds,
            VersionContext = versionContext,
            Preconditions = preconditions.IsDefault ? ImmutableArray<FeatureEditPrecondition>.Empty : preconditions
        };

    public int TotalOperations =>
        !Operations.IsDefaultOrEmpty
            ? Operations.Length
            : (Creates.IsDefault ? 0 : Creates.Length) +
              (Updates.IsDefault ? 0 : Updates.Length) +
              (Deletes.IsDefault ? 0 : Deletes.Length);

    public bool IsEmpty => TotalOperations == 0;
}

public enum FeatureEditOperationKind
{
    Create,

    Update,

    Delete
}

public readonly record struct FeatureEditOperation
{
    public required FeatureEditOperationKind Kind { get; init; }

    public Feature? Feature { get; init; }

    public long? ObjectId { get; init; }

    public static FeatureEditOperation Create(Feature feature)
        => new()
        {
            Kind = FeatureEditOperationKind.Create,
            Feature = feature
        };

    public static FeatureEditOperation Update(Feature feature)
        => new()
        {
            Kind = FeatureEditOperationKind.Update,
            Feature = feature
        };

    public static FeatureEditOperation Delete(long objectId)
        => new()
        {
            Kind = FeatureEditOperationKind.Delete,
            ObjectId = objectId
        };
}

public readonly record struct FeatureEditResult
{
    private const string RollbackErrorMessage = "Operation rolled back.";
    public required int CreatedCount { get; init; }

    public required int UpdatedCount { get; init; }

    public required int DeletedCount { get; init; }

    public ImmutableArray<long> CreatedIds { get; init; }

    public ImmutableArray<EditOperationResult> CreateResults { get; init; }

    public ImmutableArray<EditOperationResult> UpdateResults { get; init; }

    public ImmutableArray<EditOperationResult> DeleteResults { get; init; }

    public bool WasRolledBack { get; init; }

    public bool MayHaveCommitted => !WasRolledBack &&
        (CreatedCount + UpdatedCount + DeletedCount > 0 ||
         CreateResults.Any(static result => result.IsCommitOutcomeUnknown) ||
         UpdateResults.Any(static result => result.IsCommitOutcomeUnknown) ||
         DeleteResults.Any(static result => result.IsCommitOutcomeUnknown));

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
            CreateResults = NormalizeRollbackResults(createResults, includeObjectId: false),
            UpdateResults = NormalizeRollbackResults(updateResults, includeObjectId: true),
            DeleteResults = NormalizeRollbackResults(deleteResults, includeObjectId: true),
            WasRolledBack = true
        };

    // Callers must not release the idempotency reservation when the commit acknowledgement was lost.
    public static FeatureEditResult FailureWithUnknownCommitOutcome(
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
            WasRolledBack = false
        };

    public bool IsSuccess => !WasRolledBack && !HasErrors;

    public bool HasErrors =>
        CreateResults.Any(r => !r.IsSuccess) ||
        UpdateResults.Any(r => !r.IsSuccess) ||
        DeleteResults.Any(r => !r.IsSuccess);

    private static ImmutableArray<EditOperationResult> NormalizeRollbackResults(
        ImmutableArray<EditOperationResult> results,
        bool includeObjectId)
    {
        if (results.IsDefaultOrEmpty)
        {
            return ImmutableArray<EditOperationResult>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<EditOperationResult>(results.Length);
        foreach (var result in results)
        {
            if (result.IsSuccess)
            {
                builder.Add(EditOperationResult.Failure(
                    RollbackErrorMessage,
                    objectId: includeObjectId ? result.ObjectId : null));
            }
            else
            {
                builder.Add(result);
            }
        }

        return builder.ToImmutable();
    }
}

public readonly record struct EditOperationResult
{
    public const int PreconditionFailedErrorCode = 412;

    // A row-level security rejection means the operation was not applied.
    public const int NotPermittedErrorCode = 403;

    public long? ObjectId { get; init; }

    public string? GlobalId { get; init; }

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public int ErrorCode { get; init; }

    public bool IsPreconditionFailure { get; init; }

    [JsonIgnore]
    public Feature? PreconditionFailureFeature { get; init; }

    // Callers must keep the idempotency reservation while the commit outcome is unknown, because the database may have committed.
    public bool IsCommitOutcomeUnknown { get; init; }

    public EditOperationResult()
    {
    }

    public static EditOperationResult Success(long objectId, string? globalId = null)
        => new()
        {
            ObjectId = objectId,
            GlobalId = globalId,
            IsSuccess = true
        };

    public static EditOperationResult Failure(string errorMessage, int errorCode = 1000, long? objectId = null)
        => new()
        {
            ObjectId = objectId,
            IsSuccess = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode
        };

    public static EditOperationResult FailureWithUnknownCommitOutcome(
        string errorMessage,
        int errorCode = 1000,
        long? objectId = null)
        => new()
        {
            ObjectId = objectId,
            IsSuccess = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            IsCommitOutcomeUnknown = true
        };

    public static EditOperationResult PreconditionFailed(long? objectId, string? errorMessage = null)
        => new()
        {
            ObjectId = objectId,
            IsSuccess = false,
            ErrorMessage = errorMessage ?? "The feature was modified by another writer; the supplied precondition no longer matches.",
            ErrorCode = PreconditionFailedErrorCode,
            IsPreconditionFailure = true
        };
}
