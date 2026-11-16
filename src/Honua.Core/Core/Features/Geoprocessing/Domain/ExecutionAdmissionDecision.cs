// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public enum ExecutionAdmissionOutcome
{
    Admitted,

    Throttled,

    Denied
}

public enum ExecutionAdmissionDimension
{
    Rate,

    Concurrency,

    Cost,

    Backpressure
}

public sealed record ExecutionAdmissionSnapshot
{
    public int ActiveJobsInPartition { get; init; }

    public int ActiveJobsGlobal { get; init; }

    public int SubmissionsInWindow { get; init; }

    public double ActiveCostWeightInPartition { get; init; }
}

public sealed record ExecutionAdmissionDecision
{
    public required ExecutionAdmissionOutcome Outcome { get; init; }

    public ExecutionAdmissionDimension? DenyingDimension { get; init; }

    public string? PolicyRef { get; init; }

    public string? Reason { get; init; }

    public int? RetryAfterSeconds { get; init; }

    public required ExecutionAdmissionSnapshot Snapshot { get; init; }

    public static ExecutionAdmissionDecision Admitted(ExecutionAdmissionSnapshot snapshot) => new()
    {
        Outcome = ExecutionAdmissionOutcome.Admitted,
        Snapshot = snapshot
    };

    public static ExecutionAdmissionDecision Throttled(
        ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfterSeconds,
        ExecutionAdmissionSnapshot snapshot) => new()
        {
            Outcome = ExecutionAdmissionOutcome.Throttled,
            DenyingDimension = dimension,
            PolicyRef = policyRef,
            Reason = reason,
            RetryAfterSeconds = retryAfterSeconds,
            Snapshot = snapshot
        };

    public static ExecutionAdmissionDecision Denied(
        ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfterSeconds,
        ExecutionAdmissionSnapshot snapshot) => new()
        {
            Outcome = ExecutionAdmissionOutcome.Denied,
            DenyingDimension = dimension,
            PolicyRef = policyRef,
            Reason = reason,
            RetryAfterSeconds = retryAfterSeconds,
            Snapshot = snapshot
        };
}
