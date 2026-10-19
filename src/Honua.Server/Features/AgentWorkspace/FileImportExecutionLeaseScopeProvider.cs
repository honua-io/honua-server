// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Import.FileImport.Durable;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.AgentWorkspace;

// Initial new-table, verified resumable-source adapter. It grants no existing-table
// mutation or arbitrary object URI authority. Not registered before integrated qualification.
internal sealed class FileImportExecutionLeaseScopeProvider(IAgentWorkspaceStore workspaces,
    IHttpContextAccessor accessor, IConfiguration configuration, IDatabaseSessionFactory sessions,
    IServiceProvider services, TimeProvider clock) : IJobExecutionLeaseScopeProvider
{
    private const int MaximumBindingBytes = 65536;
    private readonly DatasetImportDestinationAuthorizer _destination = new(accessor, configuration, sessions);
    public ExecutionJobKind Kind => ExecutionJobKind.FileImport;

    public async Task<JobExecutionLeaseScope> PrepareAsync(ExecutionJobRecord job, ClaimsPrincipal executor,
        CancellationToken cancellationToken = default)
    {
        var bound = await LoadAsync(job, cancellationToken).ConfigureAwait(false);
        if (job.Status != ExecutionJobStatus.Queued || job.Audit.SubmitterSecurityContext?.ExecutionLease is not null
            || bound.Task.UploadAdmissionDeadline <= clock.GetUtcNow()) throw Denied();
        var captured = await AgentJobSecurityBridge.CaptureAsync(executor,
            services.GetService<IOptions<RbacOptions>>()?.Value ?? new RbacOptions(), services,
            services.GetService<ITenantContext>(), cancellationToken).ConfigureAwait(false);
        if (JobExecutionLeaseIntentFingerprint.ComputeContext(captured) != JobExecutionLeaseIntentFingerprint.ComputeContext(bound.Prepared.ExecutorPolicyContext)) throw Denied();
        var scope = Scope(bound);
        await RequireAccessAsync(bound, executor, requireWrite: true, scope.Operations[0], cancellationToken).ConfigureAwait(false);
        await RequireUnchangedAsync(job, bound, cancellationToken).ConfigureAwait(false);
        return scope;
    }

    public async Task RequireCurrentAsync(ExecutionJobRecord job, JobExecutionLeaseScope scope, JobSecurityContext context,
        ClaimsPrincipal principal, JobExecutionLeaseObservationKind? kind, CancellationToken cancellationToken = default)
    {
        var bound = await LoadAsync(job, cancellationToken).ConfigureAwait(false);
        if (Serialize(Scope(bound)) != Serialize(scope)) throw Denied();
        if (job.Audit.SubmitterSecurityContext?.ExecutionLease is null)
        {
            if (kind is not null || job.Status != ExecutionJobStatus.Queued || bound.Task.UploadAdmissionDeadline <= clock.GetUtcNow()) throw Denied();
            var fresh = await AgentJobSecurityBridge.CaptureAsync(principal,
                services.GetService<IOptions<RbacOptions>>()?.Value ?? new RbacOptions(), services,
                services.GetService<ITenantContext>(), cancellationToken).ConfigureAwait(false);
            if (JobExecutionLeaseIntentFingerprint.ComputeContext(fresh) != JobExecutionLeaseIntentFingerprint.ComputeContext(bound.Prepared.ExecutorPolicyContext)) throw Denied();
        }
        else
        {
            if (JobSecurityScope.Current is not { Submitter: { } active } execution || execution.JobId != job.OperationId
                || execution.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(job)
                || active.ExecutionLease != job.Audit.SubmitterSecurityContext.ExecutionLease
                || active.CurrentAgentPrincipal is null || !JobExecutionLeaseTransientAuthority.IsLeasePrincipal(active.CurrentAgentPrincipal)
                || JobExecutionLeaseIntentFingerprint.ComputeContext(context) != JobExecutionLeaseIntentFingerprint.ComputeContext(active)
                || kind is null && !ReferenceEquals(principal, active.CurrentAgentPrincipal)) throw Denied();
        }
        if (kind is not null && !Enum.IsDefined(kind.Value)) throw Denied();
        await RequireAccessAsync(bound, principal, kind is null, kind is null ? scope.Operations[0] : null, cancellationToken).ConfigureAwait(false);
        await RequireUnchangedAsync(job, bound, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireAccessAsync(BoundImport bound, ClaimsPrincipal principal, bool requireWrite,
        OperatorAuthorizationRequest? operation, CancellationToken cancellationToken)
    {
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId;
        var isExecutor = actor == bound.Task.OwnerActorId;
        var isInitiator = actor is not null && actor == bound.Prepared.InitiatingHumanActorId && actor == bound.Document.OwnerId
            && bound.Document.AgentActorId == bound.Task.OwnerActorId;
        if (actor is null || (!isExecutor && (!isInitiator || requireWrite))
            || !AgentWorkspaceState.CanRead(bound.Document, actor, bound.Task.TenantId, bound.Task.TargetId)
            || !await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false)
            || !await _destination.IsAuthorizedCoreAsync(services, principal, bound.Task.TargetId, bound.Task.Destination,
                bound.Task.Options, requireWrite, cancellationToken, operation).ConfigureAwait(false)
            || !await DatasetImportTaskProvider.SourceAuthorizedAsync(services, principal, bound.Task.TenantId,
                bound.Document, bound.Task.Source, cancellationToken).ConfigureAwait(false)) throw Denied();
        var storage = services.GetRequiredService<ICloudFileStorage>();
        try
        {
            StagedImportSourceMaterializer.RequireGeneration(
                await storage.GetMetadataAsync(bound.Source.FileId, cancellationToken).ConfigureAwait(false), bound.Source);
        }
        catch (InvalidDataException exception)
        {
            throw new UnauthorizedAccessException("The exact prepared source generation is no longer current.", exception);
        }
        if (!await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false)) throw Denied();
    }

    private async Task<BoundImport> LoadAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.Spec.Kind != Kind || job.Audit.OperationInstanceId != job.OperationId
            || !FileImportJobParameters.TryParse(job.Spec.Parameters, out var request, out var bytes, out _)
            || request?.StagedSource is not { Transfer: { } transfer } source || request.Acceptance is null
            || request.UsesObjectStore || request.Acceptance.OperationId != job.OperationId) throw Denied();
        var document = await workspaces.GetAsync(transfer.WorkspaceId).ConfigureAwait(false) ?? throw Denied();
        var target = await workspaces.GetTargetIdAsync().ConfigureAwait(false);
        if (document.TargetId != target || transfer.TargetId != target
            || !document.DatasetImportTasks.TryGetValue(transfer.TaskId, out var versions)
            || versions.SingleOrDefault(task => task.Revision == transfer.TaskRevision) is not { } task
            || !document.DatasetImportPreparedAdmissions.TryGetValue(transfer.TaskId, out var prepared)
            || !document.DatasetImportExecutionBindings.TryGetValue(transfer.TaskId, out var authority)
            || task.WorkspaceId != document.WorkspaceId || task.TargetId != target || task.TenantId != document.TenantId
            || task.ContentHash != transfer.TaskContentHash || task.OwnerActorId != transfer.OwnerActorId
            || !AgentWorkspaceState.CanRead(document, task.OwnerActorId, task.TenantId, target)
            || prepared.OperationId != job.OperationId || authority.AuthorityRevision != prepared.AuthorityRevision
            || authority.AuthorityFingerprint != prepared.AuthorityFingerprint || authority.PolicyContextFingerprint != prepared.PolicyContextFingerprint
            || authority.TaskRevision != task.Revision || authority.TaskContentHash != task.ContentHash
            || authority.ExecutorActorId != task.OwnerActorId || authority.InitiatingHumanActorId != prepared.InitiatingHumanActorId
            || authority.OriginatingTurnId != prepared.OriginatingTurnId || authority.OriginalTurnHash != prepared.OriginalTurnHash
            || !document.Turns.TryGetValue(prepared.OriginatingTurnId, out var turn)
            || turn.ActorId != (prepared.InitiatingHumanActorId ?? prepared.ExecutorActorId)
            || prepared.OriginalTurnHash != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)))
            || JobExecutionLeaseIntentFingerprint.ComputeContext(authority.ExecutorPolicyContext with { AgentCredential = authority.ExecutorAuthority })
                != JobExecutionLeaseIntentFingerprint.ComputeContext(prepared.ExecutorPolicyContext)
            || job.Audit.RequestedBy != task.OwnerActorId || job.Audit.TenantId != task.TenantId
            || job.AcceptanceDeadline != prepared.AcceptBefore || job.Audit.SubmitterSecurityContext is not { } original
            || JobExecutionLeaseIntentFingerprint.ComputeContext(original) != JobExecutionLeaseIntentFingerprint.ComputeContext(prepared.ExecutorPolicyContext)) throw Denied();
        if (prepared.InitiatingHumanActorId is { } human && (document.OwnerId != human || document.AgentActorId != task.OwnerActorId
            || !AgentWorkspaceState.CanRead(document, human, task.TenantId, target))) throw Denied();
        DatasetImportSubmissionIntent intent;
        try { intent = DatasetImportSubmissionIntent.Create(task, prepared, source); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new UnauthorizedAccessException("The exact prepared import intent is no longer current.", exception);
        }
        var projected = FileImportJobParameters.Build(intent.Request with
        {
            AuditId = job.Audit.AuditId, ProposalId = job.Audit.ProposalId, CorrelationId = job.Audit.CorrelationId
        }, bytes);
        if (bytes != task.Source.TotalBytes || projected.Any(pair => !job.Spec.Parameters.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
            || job.Spec.Parameters.Keys.Any(key => key.StartsWith("import.", StringComparison.Ordinal)
                && key != FileImportJobParameters.Runner && !projected.ContainsKey(key))) throw Denied();
        return new(document, task, prepared, source, new(1, task.WorkspaceId, task.TaskId, task.Revision, task.ContentHash,
            target, HashPrepared(prepared), Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source,
                FileImportExecutionScopeJsonContext.Default.ImportStagedSourceBinding)))));
    }

    private async Task RequireUnchangedAsync(ExecutionJobRecord job, BoundImport original, CancellationToken cancellationToken)
    {
        var latest = await LoadAsync(job, cancellationToken).ConfigureAwait(false);
        if (latest.Binding != original.Binding || latest.Document.OwnerId != original.Document.OwnerId
            || latest.Document.AgentActorId != original.Document.AgentActorId) throw Denied();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static JobExecutionLeaseScope Scope(BoundImport bound) => new()
    {
        Kind = ExecutionJobKind.FileImport, Resources = [],
        Operations = [new SemanticImportAuthorizationResource(bound.Task.TargetId, true).OperatorRequest],
        ProviderBinding = SerializeBinding(bound.Binding)
    };

    private static string HashPrepared(DatasetImportPreparedAdmission prepared)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared, AgentWorkspaceJsonContext.Default.DatasetImportPreparedAdmission);
        if (bytes.Length > 1048576) throw Denied();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string SerializeBinding(FileImportExecutionScopeBinding binding)
    {
        var json = JsonSerializer.Serialize(binding, FileImportExecutionScopeJsonContext.Default.FileImportExecutionScopeBinding);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBindingBytes) throw Denied();
        return json;
    }

    private static string Serialize(JobExecutionLeaseScope scope)
        => JsonSerializer.Serialize(scope, FileImportExecutionScopeJsonContext.Default.JobExecutionLeaseScope);

    private static UnauthorizedAccessException Denied() => new("Current exact prepared import source and destination authority is required.");
    private sealed record BoundImport(AgentWorkspaceDocument Document, DatasetImportTask Task,
        DatasetImportPreparedAdmission Prepared, ImportStagedSourceBinding Source, FileImportExecutionScopeBinding Binding);
}

internal sealed record FileImportExecutionScopeBinding(int Version, string WorkspaceId, string TaskId, long TaskRevision,
    string TaskContentHash, string TargetId, string PreparationFingerprint, string SourceFingerprint);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FileImportExecutionScopeBinding))]
[JsonSerializable(typeof(JobExecutionLeaseScope))]
[JsonSerializable(typeof(ImportStagedSourceBinding))]
internal sealed partial class FileImportExecutionScopeJsonContext : JsonSerializerContext;
