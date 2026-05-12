// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// MVP saved-map conflict policy based on documented operation families.
/// </summary>
/// <remarks>
/// Safe families are metadata, viewport, layer visibility, layer ordering, and style patch operations.
/// Whole WebMap document replacement is unsafe when either the proposed operation or a concurrent
/// operation uses that family. The policy can be replaced once WebMapDoc field-level transforms land.
/// </remarks>
public sealed class MvpSavedMapOperationConflictPolicy : ISavedMapOperationConflictPolicy
{
    /// <inheritdoc />
    public SavedMapOperationConflictResponse? DetectConflict(
        SavedMapOperationAppendRequest request,
        IReadOnlyList<SavedMapOperationEnvelope> concurrentOperations,
        SavedMapOperationCursor headCursor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(concurrentOperations);

        if (concurrentOperations.Count == 0)
        {
            return null;
        }

        var proposedFamily = request.Kind.GetFamily();
        var unsafeConcurrentOperations = concurrentOperations
            .Where(operation => operation.Family == SavedMapOperationFamily.WebMapDocument)
            .ToArray();

        if (proposedFamily != SavedMapOperationFamily.WebMapDocument && unsafeConcurrentOperations.Length == 0)
        {
            return null;
        }

        var conflicts = proposedFamily == SavedMapOperationFamily.WebMapDocument
            ? concurrentOperations
            : unsafeConcurrentOperations;

        return new SavedMapOperationConflictResponse
        {
            Code = "saved-map.operation.conflict",
            Message = "Operation cannot be merged safely from the supplied base cursor. Replay or resync before retrying.",
            BaseCursor = request.BaseCursor,
            HeadCursor = headCursor,
            ConflictingOperations = conflicts,
        };
    }
}
