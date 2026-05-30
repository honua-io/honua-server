// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing;

/// <summary>
/// Raised when a geoprocessing authorization check fails.
/// </summary>
internal sealed class GeoprocessingAuthorizationException : Exception
{
    /// <summary>
    /// Whether the caller needs authentication (vs. insufficient permissions).
    /// </summary>
    public bool RequiresAuthentication { get; }

    public GeoprocessingAuthorizationException(bool requiresAuthentication)
        : base(requiresAuthentication
            ? "Authentication is required for this operation."
            : "You do not have permission to perform this operation.")
    {
        RequiresAuthentication = requiresAuthentication;
    }
}

/// <summary>
/// Raised when an approval gate blocks the operation.
/// </summary>
internal sealed class GeoprocessingApprovalRequiredException : Exception
{
    /// <summary>
    /// The policy reference that requires approval.
    /// </summary>
    public string PolicyRef { get; }

    public GeoprocessingApprovalRequiredException(string policyRef, string? detail = null)
        : base($"This operation requires approval (policy: {policyRef}). " +
               (detail ?? "Use ValidatePlan to check approval requirements before submission."))
    {
        PolicyRef = policyRef;
    }
}

/// <summary>
/// Raised when a requested job or entity is not found.
/// </summary>
internal sealed class GeoprocessingNotFoundException : Exception
{
    public GeoprocessingNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Raised when a precondition is not met (e.g., cancelling a terminal job).
/// </summary>
internal sealed class GeoprocessingPreconditionFailedException : Exception
{
    public GeoprocessingPreconditionFailedException(string message) : base(message) { }
}

/// <summary>
/// Raised when plan validation fails structurally.
/// </summary>
internal sealed class GeoprocessingValidationException : Exception
{
    public GeoprocessingValidationException(string message) : base(message) { }
}

/// <summary>
/// Raised when the durable job store is unavailable.
/// </summary>
internal sealed class GeoprocessingStoreUnavailableException : Exception
{
    public GeoprocessingStoreUnavailableException()
        : base("Job operations require Redis-backed durable storage. " +
               "Ensure a valid Redis connection is configured.")
    { }
}

/// <summary>
/// Raised when an idempotency key collision is detected with a different request.
/// </summary>
internal sealed class GeoprocessingIdempotencyConflictException : Exception
{
    public GeoprocessingIdempotencyConflictException()
        : base("Idempotency key is already associated with a different request.") { }
}

/// <summary>
/// Raised when a runtime admission control blocks job submission.
/// Both Throttled and Denied outcomes surface through this exception; the outcome
/// and dimension are preserved for protocol mapping, telemetry, and eval harness signals.
/// </summary>
internal sealed class GeoprocessingAdmissionException : Exception
{
    /// <summary>
    /// Terminal admission outcome (<see cref="Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionOutcome.Throttled"/>
    /// or <see cref="Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionOutcome.Denied"/>).
    /// </summary>
    public Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionOutcome Outcome { get; }

    /// <summary>
    /// Control dimension that rejected the request.
    /// </summary>
    public Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionDimension DenyingDimension { get; }

    /// <summary>
    /// Machine-readable policy reference that rejected the request.
    /// </summary>
    public string PolicyRef { get; }

    /// <summary>
    /// Suggested retry delay in seconds.
    /// </summary>
    public int RetryAfterSeconds { get; }

    public GeoprocessingAdmissionException(
        Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionOutcome outcome,
        Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfterSeconds)
        : base(reason)
    {
        Outcome = outcome;
        DenyingDimension = dimension;
        PolicyRef = policyRef;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
