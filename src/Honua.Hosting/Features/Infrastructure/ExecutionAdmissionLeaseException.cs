// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Raised when the shared execution-admission lease cannot be taken or confirmed.
/// Protocol adapters translate this into their own admission exception; the coordinator
/// does not throw a protocol-specific type.
/// </summary>
internal sealed class ExecutionAdmissionLeaseException : Exception
{
    /// <summary>
    /// Terminal admission outcome (<see cref="ExecutionAdmissionOutcome.Denied"/>).
    /// </summary>
    public ExecutionAdmissionOutcome Outcome { get; }

    /// <summary>
    /// Control dimension that rejected the request.
    /// </summary>
    public ExecutionAdmissionDimension DenyingDimension { get; }

    /// <summary>
    /// Machine-readable policy reference that rejected the request.
    /// </summary>
    public string PolicyRef { get; }

    /// <summary>
    /// Suggested retry delay in seconds.
    /// </summary>
    public int RetryAfterSeconds { get; }

    public ExecutionAdmissionLeaseException(
        ExecutionAdmissionOutcome outcome,
        ExecutionAdmissionDimension dimension,
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
