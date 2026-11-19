// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// A stored submitter snapshot decides what background work may read, so the record carries a seal
// over it, bound to that record and the work it authorizes. A snapshot whose seal does not verify is
// not used: the work then has no submitter context and every reader refuses it. The unverified
// snapshot is kept and written back unchanged, so a node without the key ring does not erase what it
// cannot check. A host with no protector neither seals nor checks.
internal static class ExecutionJobSubmitterSeal
{
    // Sealed before job secrets are protected and verified after they are read back, so the spec is bound in plain form.
    public static ExecutionJobRecord Seal(ExecutionJobRecord job, IExecutionJobSecretProtector protector)
        => job with { Audit = SealAudit(job.Audit, JobBinding(job), protector) };

    public static ExecutionJobRecord Verify(ExecutionJobRecord job, IExecutionJobSecretProtector protector)
        => job with { Audit = VerifyAudit(job.Audit, JobBinding(job), protector) };

    public static OperationAuditInfo SealAudit(OperationAuditInfo audit, string binding, IExecutionJobSecretProtector protector)
    {
        if (Bypassed(protector))
        {
            return audit;
        }

        if (audit.SubmitterSecurityContext is { } submitter)
        {
            return audit with { SubmitterSecurityContextSeal = SealSnapshot(protector, binding, submitter) };
        }

        return audit.UnverifiedSubmitterSecurityContext is { } unverified
            ? audit with { SubmitterSecurityContext = unverified }
            : audit with { SubmitterSecurityContextSeal = null };
    }

    // A verified seal is dropped from the record read back: it is recomputed on every write.
    public static OperationAuditInfo VerifyAudit(OperationAuditInfo audit, string binding, IExecutionJobSecretProtector protector)
    {
        if (Bypassed(protector) || audit.SubmitterSecurityContext is not { } submitter)
        {
            return audit;
        }

        return Verifies(protector, binding, new StoredSnapshot(submitter, audit.SubmitterSecurityContextSeal))
            ? audit with { SubmitterSecurityContextSeal = null }
            : audit with { SubmitterSecurityContext = null, UnverifiedSubmitterSecurityContext = submitter };
    }

    public static bool Bypassed(IExecutionJobSecretProtector protector)
        => ReferenceEquals(protector, UnprotectedExecutionJobSecrets.Instance);

    public static string SealSnapshot(IExecutionJobSecretProtector protector, string binding, JobSecurityContext snapshot)
        => protector.Seal(JobSecurityContextDigest.Compute(binding, snapshot));

    public static bool Verifies(IExecutionJobSecretProtector protector, string binding, StoredSnapshot stored)
        => stored.Seal is not null && protector.VerifySeal(JobSecurityContextDigest.Compute(binding, stored.Snapshot), stored.Seal);

    private static string JobBinding(ExecutionJobRecord job)
        => JobSecurityContextDigest.Binding(
            "honua.execution-job.submitter.v1",
            job.OperationId,
            job.Audit.RequestedBy,
            JsonSerializer.Serialize(job.Spec, ControlPlaneJsonContext.Default.ExecutionJobSpec));
}

// A stored snapshot and the seal stored with it.
internal readonly record struct StoredSnapshot(JobSecurityContext Snapshot, string? Seal);
