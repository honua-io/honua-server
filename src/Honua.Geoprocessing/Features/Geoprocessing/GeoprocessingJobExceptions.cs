// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

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

    /// <summary>
    /// The resource type the denied check evaluated, when known. Lets protocol adapters
    /// emit a security-log entry naming the real resource rather than a hard-coded default.
    /// </summary>
    public OperatorResourceType? ResourceType { get; }

    /// <summary>
    /// The operation the denied check evaluated, when known. Carried so a mutating-process
    /// (or custom-code) denial is logged as that operation instead of an indistinguishable
    /// baseline <see cref="OperatorOperation.Execute"/> denial (#2798).
    /// </summary>
    public OperatorOperation? Operation { get; }

    public GeoprocessingAuthorizationException(bool requiresAuthentication)
        : base(requiresAuthentication
            ? "Authentication is required for this operation."
            : "You do not have permission to perform this operation.")
    {
        RequiresAuthentication = requiresAuthentication;
    }

    /// <summary>
    /// Creates an authorization failure with a caller-supplied message (e.g. one
    /// naming the specific permission the caller lacks) so structured error
    /// contracts can surface an actionable denial, and records the resource/operation
    /// the check evaluated so adapters log the real denied operation.
    /// </summary>
    /// <param name="requiresAuthentication">Whether the caller needs authentication (vs. insufficient permissions).</param>
    /// <param name="message">The denial message surfaced to the caller.</param>
    /// <param name="resourceType">The resource type the denied check evaluated, when known.</param>
    /// <param name="operation">The operation the denied check evaluated, when known.</param>
    public GeoprocessingAuthorizationException(
        bool requiresAuthentication,
        string message,
        OperatorResourceType? resourceType = null,
        OperatorOperation? operation = null)
        : base(message)
    {
        RequiresAuthentication = requiresAuthentication;
        ResourceType = resourceType;
        Operation = operation;
    }
}

/// <summary>
/// Raised when an approval gate blocks the operation. When a durable operation
/// proposal was persisted for the gated plan (ADR-0064, #2814), <see cref="ProposalId"/>
/// carries the proposal identifier so an adapter can surface the
/// <c>honua://proposals/{id}</c> resume path instead of dead-ending the caller.
/// </summary>
internal sealed class GeoprocessingApprovalRequiredException : Exception
{
    /// <summary>
    /// The policy reference that requires approval.
    /// </summary>
    public string PolicyRef { get; }

    /// <summary>
    /// Identifier of the persisted <c>AwaitingApproval</c> proposal for the gated plan,
    /// when the approval lane persisted one. Null when no proposal was created (for
    /// example when the durable proposal surface is unavailable and the submission
    /// hard-fails). Adapters surface a <c>honua://proposals/{id}</c> resume path from it.
    /// </summary>
    public string? ProposalId { get; }

    public GeoprocessingApprovalRequiredException(string policyRef, string? detail = null, string? proposalId = null)
        : base($"This operation requires approval (policy: {policyRef}). " +
               (proposalId != null
                   ? $"A proposal was created and is awaiting approval (proposalId: {proposalId})."
                   : detail ?? "Use ValidatePlan to check approval requirements before submission."))
    {
        PolicyRef = policyRef;
        ProposalId = proposalId;
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

    /// <summary>
    /// Creates the exception with a custom message for adapters that surface
    /// other unavailable upstream dependencies (e.g. geocoding providers)
    /// through the same retryable <c>unavailable</c> error channel.
    /// </summary>
    public GeoprocessingStoreUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Raised when an idempotency key collision is detected with a different request.
/// </summary>
internal sealed class GeoprocessingIdempotencyConflictException : Exception
{
    public GeoprocessingIdempotencyConflictException(string? conflictingJobId = null)
        : base("Idempotency key is already associated with a different request.")
    {
        ConflictingJobId = conflictingJobId;
    }

    /// <summary>
    /// Identifier of the job that already owns the idempotency key, so a client
    /// can recover by inspecting the winning job rather than blindly retrying.
    /// </summary>
    public string? ConflictingJobId { get; }
}

/// <summary>
/// Raised when a geoprocessing output write would collide with an existing,
/// available artifact in the target workspace and the caller did not request
/// <c>env:overwriteOutput=true</c>. Mirrors arcpy's default
/// <c>arcpy.env.overwriteOutput = False</c> behavior: re-running a tool against
/// the same workspace output fails clearly instead of silently clobbering it.
/// </summary>
internal sealed class ArtifactAlreadyExistsException : Exception
{
    /// <summary>
    /// Identifier of the workspace containing the colliding output.
    /// </summary>
    public string WorkspaceId { get; }

    /// <summary>
    /// Stable output label that already exists in the workspace.
    /// </summary>
    public string Label { get; }

    public ArtifactAlreadyExistsException(string workspaceId, string label)
        : base(
            $"Output '{label}' already exists in workspace '{workspaceId}'. " +
            "Set env:overwriteOutput=true to replace it.")
    {
        WorkspaceId = workspaceId;
        Label = label;
    }
}

/// <summary>
/// Raised when an <c>env:overwriteOutput=true</c> replacement cannot complete
/// because the artifact store failed to delete the colliding artifact
/// (<c>DeleteAsync</c> returned <c>false</c>). Rather than proceeding to add a
/// second <c>Available</c> artifact under the same label — leaving the workspace
/// with a duplicate or half-replaced output — the replacement is aborted and
/// this is surfaced through the same curated failure channel as
/// <see cref="ArtifactAlreadyExistsException"/>.
/// </summary>
internal sealed class ArtifactReplacementFailedException : Exception
{
    /// <summary>
    /// Identifier of the workspace whose replacement could not complete.
    /// </summary>
    public string WorkspaceId { get; }

    /// <summary>
    /// Stable output label whose existing artifact could not be deleted.
    /// </summary>
    public string Label { get; }

    public ArtifactReplacementFailedException(string workspaceId, string label)
        : base(
            $"Output '{label}' in workspace '{workspaceId}' could not be replaced: " +
            "the existing artifact could not be deleted, so the replacement was aborted " +
            "to avoid leaving a duplicate output. Retry, or remove the existing output before re-running.")
    {
        WorkspaceId = workspaceId;
        Label = label;
    }
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
