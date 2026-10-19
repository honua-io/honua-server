// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>Shape checks only; grants no execution or observation authority.</summary>
public static class JobExecutionLeaseObservationEligibility
{
    /// <summary>Allows retained terminal facts or metadata of an unresolved accepted import.</summary>
    public static bool AllowsRetainedFact(ExecutionJobRecord job, JobExecutionLeaseObservationKind kind)
        => Enum.IsDefined(kind) && (job.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled
            || kind == JobExecutionLeaseObservationKind.Status && job.Spec.Kind == ExecutionJobKind.FileImport
                && job.Status is ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running
                && job.CommitRecovery is { AttemptCount: > 0 } pending && !string.IsNullOrWhiteSpace(pending.WorkerId)
                && job.Audit.SubmitterSecurityContext?.ExecutionLease is not null
                && pending.IntentFingerprint == JobExecutionLeaseIntentFingerprint.Compute(job));
}
