// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
