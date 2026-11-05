// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal sealed class ExecutionAdmissionLeaseException : Exception
{
    public ExecutionAdmissionOutcome Outcome { get; }

    public ExecutionAdmissionDimension DenyingDimension { get; }

    public string PolicyRef { get; }

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
