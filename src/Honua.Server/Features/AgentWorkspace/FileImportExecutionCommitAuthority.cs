// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Import.FileImport.Durable;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.AgentWorkspace;

// Only the actual accepted new-table worker scope can cross this commit boundary.
// Registration remains gated on integrated qualification; receipt recovery is separate.
internal sealed class FileImportExecutionCommitAuthority(IExecutionJobStore jobs,
    IJobExecutionLeaseAuthority leases) : IImportExecutionCommitAuthority
{
    public async Task<ImportExecutionCommitBinding> RequireCurrentAsync(ImportRequest request,
        string physicalSchema, string physicalTable, CancellationToken cancellationToken = default)
    {
        if (JobSecurityScope.Current is not { JobId: { } id, IntentFingerprint: { } fingerprint,
            WorkerId: { } worker, AttemptCount: { } attempt,
            Submitter: { ExecutionLease: { } envelope, CurrentAgentPrincipal: { } principal } active }
            || attempt < 1 || !JobExecutionLeaseTransientAuthority.IsLeasePrincipal(principal)) throw Denied();
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        RequireJob(job, id, fingerprint, active, envelope, worker, attempt);
        if (job.Spec.Kind != ExecutionJobKind.FileImport
            || !FileImportJobParameters.TryParse(job.Spec.Parameters, out var original, out _, out _)
            || original?.StagedSource is not { Transfer: not null } source || original.Acceptance is null
            || original.Acceptance.OperationId != id || request.StagedSource != source
            || physicalSchema != original.TargetSchema
            || physicalTable != ImportPhysicalTableName.Canonical(original.TableName)
            || request.LocalFilePath is not null || request.ObjectStoreUri is not null
            || request.FileStream is null || request.CloudFileId is not null
            || !request.KeyColumns.SequenceEqual(original.KeyColumns, StringComparer.Ordinal)) throw Denied();
        // Materialization changes only the source handle. Every original option,
        // ownership, generation, acceptance deadline and audit field stays pinned.
        var normalized = request with { FileStream = null, CloudFileId = original.CloudFileId, KeyColumns = original.KeyColumns };
        if (normalized != original) throw Denied();
        var current = await leases.RevalidateForExecutionAsync(job, cancellationToken).ConfigureAwait(false);
        if (current.ExecutionLease != envelope
            || JobExecutionLeaseIntentFingerprint.ComputeContext(current) != JobExecutionLeaseIntentFingerprint.ComputeContext(active)) throw Denied();
        var latest = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        RequireJob(latest, id, fingerprint, active, envelope, worker, attempt);
        cancellationToken.ThrowIfCancellationRequested();
        // The current FileImport scope provider authorizes only absent destinations,
        // using its own database session rather than the importer's transaction.
        return new(id, fingerprint, RequireAbsentTarget: true);
    }

    private static void RequireJob(ExecutionJobRecord job, string id, string fingerprint,
        JobSecurityContext active, JobExecutionLease envelope, string worker, int attempt)
    {
        if (job.OperationId != id || job.ClaimedBy != worker || job.AttemptCount != attempt
            || JobExecutionLeaseIntentFingerprint.Compute(job) != fingerprint
            || job.CancellationRequestedAt is not null || job.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Running)
            || job.Audit.SubmitterSecurityContext is not { } original || original.ExecutionLease != envelope
            || JobExecutionLeaseIntentFingerprint.ComputeContext(original) != JobExecutionLeaseIntentFingerprint.ComputeContext(active)) throw Denied();
    }

    private static UnauthorizedAccessException Denied() => new("Current exact accepted import execution authority is required at commit.");
}
