// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Infrastructure.Tiles;

/// <summary>
/// Raised when a tile-export submission fails structural or contract validation.
/// Adapters map this to a sanitized 400 in the protocol error envelope.
/// </summary>
internal sealed class TileExportValidationException(string message) : Exception(message);

/// <summary>
/// Raised when a tile-export job is not found, is owned by another principal, or is
/// bound to a different source/resource than the caller's lookup scope. All three
/// cases surface identically so cross-principal or cross-resource probing cannot
/// confirm that a job identifier exists (owner/resource isolation).
/// </summary>
internal sealed class TileExportNotFoundException(string message) : Exception(message);

/// <summary>
/// Raised when a lifecycle precondition is not met — for example requesting a result
/// before the job reached a successful terminal state, or cancelling a terminal job.
/// </summary>
internal sealed class TileExportPreconditionFailedException(string message) : Exception(message);

/// <summary>
/// Raised when the durable execution-job store is unavailable. Durable tile-export
/// submission requires Redis-backed persistence and configured artifact storage.
/// </summary>
internal sealed class TileExportStoreUnavailableException(string message) : Exception(message)
{
    public TileExportStoreUnavailableException()
        : this("Durable tile-export jobs require Redis-backed storage and configured artifact storage. " +
               "Ensure both are configured before submitting an asynchronous export.")
    {
    }
}

/// <summary>
/// Raised when an idempotency-key replay collides with a different request payload, or
/// when a different principal replays another caller's key. The winning job identifier
/// is withheld from cross-principal callers by the caller mapping the collision to a
/// sanitized error.
/// </summary>
internal sealed class TileExportIdempotencyConflictException(string? conflictingJobId = null)
    : Exception("Idempotency key is already associated with a different tile-export request.")
{
    /// <summary>
    /// Identifier of the job that already owns the idempotency key, populated only for a
    /// same-principal payload mismatch so the caller can inspect the winning job.
    /// </summary>
    public string? ConflictingJobId { get; } = conflictingJobId;
}

/// <summary>
/// Raised when execution admission throttles or denies a tile-export submission. Both
/// outcomes travel through this exception; the outcome and dimension are preserved so
/// adapters map throttling to <c>429</c> and saturated backpressure to <c>503</c>,
/// each with a <c>Retry-After</c> hint (matching the exportTiles queue behavior Esri
/// documents).
/// </summary>
internal sealed class TileExportAdmissionException(
    ExecutionAdmissionOutcome outcome,
    ExecutionAdmissionDimension dimension,
    string policyRef,
    string reason,
    int retryAfterSeconds) : Exception(reason)
{
    /// <summary>Terminal admission outcome (<see cref="ExecutionAdmissionOutcome.Throttled"/> or <see cref="ExecutionAdmissionOutcome.Denied"/>).</summary>
    public ExecutionAdmissionOutcome Outcome { get; } = outcome;

    /// <summary>Control dimension that rejected the request.</summary>
    public ExecutionAdmissionDimension DenyingDimension { get; } = dimension;

    /// <summary>Machine-readable policy reference that rejected the request.</summary>
    public string PolicyRef { get; } = policyRef;

    /// <summary>Suggested retry delay in seconds surfaced through <c>Retry-After</c>.</summary>
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
