// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Infrastructure.Tiles;

internal sealed class TileExportValidationException(string message) : Exception(message);

// A missing job, a job owned by someone else, and a job bound to another source are the same error, so a probe cannot confirm that an id exists.
internal sealed class TileExportNotFoundException(string message) : Exception(message);

internal sealed class TileExportPreconditionFailedException(string message) : Exception(message);

internal sealed class TileExportStoreUnavailableException(string message) : Exception(message)
{
    public TileExportStoreUnavailableException()
        : this("Durable tile-export jobs require Redis-backed storage and configured artifact storage. " +
               "Ensure both are configured before submitting an asynchronous export.")
    {
    }
}

internal sealed class TileExportIdempotencyConflictException(string? conflictingJobId = null)
    : Exception("Idempotency key is already associated with a different tile-export request.")
{
    public string? ConflictingJobId { get; } = conflictingJobId;
}

internal sealed class TileExportAdmissionException(
    ExecutionAdmissionOutcome outcome,
    ExecutionAdmissionDimension dimension,
    string policyRef,
    string reason,
    int retryAfterSeconds) : Exception(reason)
{
    public ExecutionAdmissionOutcome Outcome { get; } = outcome;

    public ExecutionAdmissionDimension DenyingDimension { get; } = dimension;

    public string PolicyRef { get; } = policyRef;

    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
