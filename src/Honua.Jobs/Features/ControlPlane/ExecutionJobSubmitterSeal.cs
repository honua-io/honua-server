// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// The submitter snapshot decides what a background job may read, so the stored record carries a seal
// over it, bound to the operation and requester. A snapshot whose seal does not verify is not used:
// the job then has no submitter context and every reader refuses it. The unverified snapshot is kept
// and written back unchanged, so a node without the key ring does not erase what it cannot check.
internal static class ExecutionJobSubmitterSeal
{
    private const string Domain = "honua.execution-job.submitter.v1";

    public static ExecutionJobRecord Seal(ExecutionJobRecord job, IExecutionJobSecretProtector protector)
    {
        if (ReferenceEquals(protector, UnprotectedExecutionJobSecrets.Instance))
        {
            return job;
        }

        if (job.Audit.SubmitterSecurityContext is { } submitter)
        {
            var seal = protector.Seal(Digest(job, submitter));
            return job with { Audit = job.Audit with { SubmitterSecurityContextSeal = seal } };
        }

        return job.Audit.UnverifiedSubmitterSecurityContext is { } unverified
            ? job with { Audit = job.Audit with { SubmitterSecurityContext = unverified } }
            : job with { Audit = job.Audit with { SubmitterSecurityContextSeal = null } };
    }

    public static ExecutionJobRecord Verify(ExecutionJobRecord job, IExecutionJobSecretProtector protector)
    {
        if (ReferenceEquals(protector, UnprotectedExecutionJobSecrets.Instance)
            || job.Audit.SubmitterSecurityContext is not { } submitter)
        {
            return job;
        }

        if (job.Audit.SubmitterSecurityContextSeal is { } seal && protector.VerifySeal(Digest(job, submitter), seal))
        {
            return job;
        }

        return job with
        {
            Audit = job.Audit with
            {
                SubmitterSecurityContext = null,
                UnverifiedSubmitterSecurityContext = submitter,
            },
        };
    }

    private static string Digest(ExecutionJobRecord job, JobSecurityContext submitter)
    {
        var snapshot = JsonSerializer.Serialize(submitter, ControlPlaneJsonContext.Default.JobSecurityContext);
        var bound = string.Join('\n', Domain, job.OperationId, job.Audit.RequestedBy ?? string.Empty, snapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bound)));
    }
}
