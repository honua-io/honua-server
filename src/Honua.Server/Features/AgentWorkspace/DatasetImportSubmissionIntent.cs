// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Import.FileImport.Resumable;

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>
/// Projects server-retained task, preparation and verified source into the canonical
/// import request. This consistency check grants no authority and creates no job.
/// Current caller, source, destination and approval checks still precede acceptance.
/// </summary>
internal sealed record DatasetImportSubmissionIntent(
    DatasetImportTask Task, DatasetImportPreparedAdmission Prepared, ImportRequest Request,
    JobSecurityContext ExecutorContext, JobExecutionLeaseAdmissionBinding Admission)
{
    internal async System.Threading.Tasks.Task RequireCurrentCallerAsync(IServiceProvider services, ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        var tasks = new ImportUploadTaskAuthority(services.GetRequiredService<IDatasetImportTaskProvider>(),
            services.GetService<IDatasetImportContributorAuthorizer>());
        var authorized = await tasks.AuthorizeAsync(caller, new(Task.WorkspaceId, Task.TaskId, Task.Revision, Task.ContentHash),
            requireWrite: true, cancellationToken).ConfigureAwait(false);
        if (authorized.Task != Task) throw new UnauthorizedAccessException("The original import task changed.");
        var admissions = services.GetRequiredService<IDatasetImportAdmissionProvider>();
        var prepared = authorized.ContributorActorId == Task.OwnerActorId
            ? await admissions.ReadAsync(caller, Task.WorkspaceId, Task.TaskId, Task.Revision, Task.ContentHash, cancellationToken).ConfigureAwait(false)
            : await admissions.ReadContributionAsync(caller, Task.WorkspaceId, Task.TaskId, Task.Revision, Task.ContentHash, cancellationToken).ConfigureAwait(false);
        if (prepared is null || JsonSerializer.Serialize(prepared, AgentWorkspaceJsonContext.Default.DatasetImportPreparedAdmission) !=
            JsonSerializer.Serialize(Prepared, AgentWorkspaceJsonContext.Default.DatasetImportPreparedAdmission))
            throw new UnauthorizedAccessException("The original prepared import authority is unavailable or changed.");
    }

    internal static DatasetImportSubmissionIntent Create(DatasetImportTask task,
        DatasetImportPreparedAdmission prepared, ImportStagedSourceBinding source)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        var transfer = source.Transfer ?? throw Mismatch();
        var context = prepared.ExecutorPolicyContext;
        if (DatasetImportTaskState.ContentHash(task) != task.ContentHash ||
            prepared.TaskId != task.TaskId || prepared.TaskRevision != task.Revision ||
            prepared.TaskContentHash != task.ContentHash || prepared.ExecutorActorId != task.OwnerActorId ||
            prepared.AcceptBefore != task.UploadAdmissionDeadline || prepared.AuthorityRevision <= 0 ||
            context.ExecutionLease is not null || context.AgentCredential is null ||
            context.OwnerActorId != task.OwnerActorId || context.PrincipalId != task.OwnerActorId || context.TenantId != task.TenantId ||
            prepared.PolicyContextFingerprint != JobSecurityContextFingerprint.Compute(context) ||
            JobExecutionLeaseIntentFingerprint.ComputeContext(context) !=
                JobExecutionLeaseIntentFingerprint.ComputeContext(context with { AgentCredential = prepared.ExecutorAuthority }) ||
            prepared.AuthorityFingerprint != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                prepared.ExecutorAuthority, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot))) ||
            !AgentWorkspaceState.IsIdentifier(prepared.OriginatingTurnId) || !DatasetImportTaskState.Hash(prepared.OriginalTurnHash) ||
            transfer.WorkspaceId != task.WorkspaceId || transfer.TaskId != task.TaskId || transfer.TaskRevision != task.Revision ||
            transfer.TaskContentHash != task.ContentHash || transfer.TargetId != task.TargetId ||
            transfer.OwnerActorId != task.OwnerActorId || transfer.TenantId != task.TenantId ||
            source.Bytes != task.Source.TotalBytes || source.Sha256 != task.Source.ExpectedSha256)
        {
            throw Mismatch();
        }

        var acceptance = new ImportAcceptanceBinding(prepared.OperationId, task.ContentHash, prepared.AcceptBefore);
        acceptance.Validate();
        var request = new ImportRequest
        {
            Acceptance = acceptance,
            StagedSource = source,
            CloudFileId = source.FileId,
            FileName = task.Source.FileName,
            SourceKind = task.Source.Kind,
            UploadId = transfer.UploadId,
            Ownership = new(task.OwnerActorId, task.TenantId),
            TableName = task.Destination.Table,
            TargetSchema = task.Destination.Schema,
            LoadMode = task.Options.LoadMode,
            OverwriteExisting = task.Options.LoadMode == ImportLoadMode.Replace,
            TargetSrid = task.Options.TargetSrid,
            SourceSrid = task.Options.SourceSrid,
            OperationInstanceId = prepared.OperationId
        };
        request.Validate();
        return new(task, prepared, request, context, new JobExecutionLeaseAdmissionBinding
        {
            WorkspaceId = task.WorkspaceId,
            TaskId = task.TaskId,
            TaskRevision = task.Revision,
            TaskContentHash = task.ContentHash,
            AuthorityRevision = prepared.AuthorityRevision,
            AuthorityFingerprint = prepared.AuthorityFingerprint,
            PolicyContextFingerprint = prepared.PolicyContextFingerprint,
            InitiatingHumanActorId = prepared.InitiatingHumanActorId,
            OriginatingTurnId = prepared.OriginatingTurnId,
            OriginalTurnHash = prepared.OriginalTurnHash
        });
    }

    private static InvalidOperationException Mismatch()
        => new("The retained task, authority preparation and source do not describe the same import.");
}
