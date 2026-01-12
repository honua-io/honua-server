// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Represents an operation progress entry that can be marked as cancelled.
/// </summary>
public interface ICancellableOperationProgress : IOperationProgress
{
    /// <summary>
    /// Returns a cancelled copy of the progress record.
    /// </summary>
    /// <param name="completedAt">Timestamp when the cancellation completed.</param>
    /// <param name="currentPhase">Optional phase description.</param>
    IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase);
}
