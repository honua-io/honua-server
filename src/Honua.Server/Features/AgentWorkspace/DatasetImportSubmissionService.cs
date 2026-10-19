// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Import;

namespace Honua.Server.Features.AgentWorkspace;

// Registration is explicit at the host composition boundary. No default activation,
// in-process fallback, caller-selected storage key, or submitted ownership exists here.
internal sealed class DatasetImportSubmissionService(IAgentWorkspaceStore store)
{
    // Composition discovery does not instantiate dependencies or claim worker readiness.
    internal static bool IsConfigured(IServiceProvider services)
    {
        var registrations = services.GetService<IServiceProviderIsService>();
        return registrations is not null && new[]
        {
            typeof(DatasetImportSubmissionService), typeof(IDatasetImportSubmissionReadiness),
            typeof(IImportJobService), typeof(BackgroundImportRunnerDecision), typeof(ImportUploadCoordinator),
            typeof(IDatasetImportAdmissionProvider), typeof(IDatasetImportAuthorityDescriptorReader),
            typeof(IExecutionJobStore), typeof(IJobExecutionLeaseAuthority), typeof(IJobExecutionLeasePolicy),
            typeof(IJobExecutionLeaseObservationPolicy), typeof(IJobExecutionLeaseScopeProvider)
        }.All(registrations.IsService);
    }

    internal static async ValueTask<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken)
        => IsConfigured(services) && await services.GetRequiredService<IDatasetImportSubmissionReadiness>()
            .IsReadyAsync(cancellationToken).ConfigureAwait(false);

    internal async Task<DatasetImportSubmissionReceipt?> ReadAsync(HttpContext context, ReadDatasetImportAuthority request)
    {
        Validate(request);
        var original = await LoadAsync(context, request).ConfigureAwait(false);
        if (original is null) return null;
        var prepared = original.Prepared;
        if (prepared is not null)
        {
            var job = await context.RequestServices.GetRequiredService<IExecutionJobStore>()
                .GetAsync(prepared.OperationId, context.RequestAborted).ConfigureAwait(false);
            if (job is not null)
            {
                // The resolver loads the actual accepted job and independently authorizes
                // this observer. Terminal human observation does not restore old execution
                // authority or require the expired/deleted source to exist again.
                var observed = await context.RequestServices.GetRequiredService<IJobExecutionLeaseAuthority>()
                    .ResolveForObservationAsync(job.OperationId, context.User, JobExecutionLeaseObservationKind.Status,
                        context.RequestAborted).ConfigureAwait(false);
                RequireAccepted(original, job, observed);
                var confirmed = await context.RequestServices.GetRequiredService<IExecutionJobStore>()
                    .GetAsync(job.OperationId, context.RequestAborted).ConfigureAwait(false);
                if (confirmed is null || confirmed.Version != job.Version) return null;
                if (confirmed.Status != job.Status || confirmed.FileImportOutput != job.FileImportOutput ||
                    confirmed.Audit.SubmitterSecurityContext?.ExecutionLease != job.Audit.SubmitterSecurityContext?.ExecutionLease ||
                    JobExecutionLeaseIntentFingerprint.Compute(confirmed) != observed.IntentFingerprint)
                    throw new UnauthorizedAccessException("The authoritative accepted receipt changed during observation.");
                if (!await CurrentAsync(context, request, original).ConfigureAwait(false)) return null;
                return Receipt(original, ProjectJob(job, observed));
            }
        }

        // Without an actual accepted record, retain the original task authority rules.
        // Preparation alone never activates the historical accepted-job observation path.
        var authorized = await Authority(context).AuthorizeAsync(context.User, Reference(request), false, context.RequestAborted).ConfigureAwait(false);
        if (authorized.Task != original.Task || !await CurrentAsync(context, request, original).ConfigureAwait(false)) return null;
        return Receipt(original);
    }

    internal async Task<DatasetImportSubmissionReceipt?> SubmitAsync(HttpContext context, SubmitDatasetImportCall? call)
    {
        if (call?.Request is not { } input || !DatasetImportTaskState.ValidAuthorityScope(call.WorkspaceId, call.TaskId, call.Revision, input.TaskContentHash) ||
            input.AuthorityRevision <= 0 || !DatasetImportTaskState.Hash(input.AuthorityFingerprint) || !DatasetImportTaskState.Hash(input.PolicyContextFingerprint))
            throw new ArgumentException("A valid original task and reviewed authority binding are required.");
        var reference = new ReadDatasetImportAuthority(call.WorkspaceId, call.TaskId, call.Revision, input.TaskContentHash);
        var original = await ReadAsync(context, reference).ConfigureAwait(false);
        if (original is null) return null;
        if (original.State == "accepted")
        {
            RequireReviewed(original, input);
            return original;
        }
        if (!await IsReadyAsync(context.RequestServices, context.RequestAborted).ConfigureAwait(false))
            throw new AgentWorkspaceUnavailableException();
        var services = context.RequestServices;
        var authorized = await Authority(context).AuthorizeAsync(context.User, Reference(reference), true, context.RequestAborted).ConfigureAwait(false);
        var descriptor = await services.GetRequiredService<IDatasetImportAuthorityDescriptorReader>()
            .ReadAsync(context.User, call.WorkspaceId, call.TaskId, call.Revision, input.TaskContentHash, context.RequestAborted).ConfigureAwait(false);
        if (descriptor is null) return null;
        if (descriptor.AuthorityRevision != input.AuthorityRevision || descriptor.AuthorityFingerprint != input.AuthorityFingerprint ||
            descriptor.PolicyContextFingerprint != input.PolicyContextFingerprint)
            throw new AgentWorkspaceConflictException("The reviewed import authority changed. Read the original task before continuing.");
        var upload = await services.GetRequiredService<ImportUploadCoordinator>()
            .GetAsync(context.User, Reference(reference), context.RequestAborted).ConfigureAwait(false);
        if (upload?.State != ImportUploadState.Sealed || upload.StagedSource is not { } source)
            throw new AgentWorkspaceConflictException("The original verified source has not been retained for import.");
        var admissions = services.GetRequiredService<IDatasetImportAdmissionProvider>();
        var prepared = authorized.ContributorActorId == authorized.Task.OwnerActorId
            ? await admissions.PrepareAsync(context.User, call.WorkspaceId, call.TaskId, call.Revision, input.TaskContentHash,
                input.AuthorityRevision, input.AuthorityFingerprint, context.RequestAborted).ConfigureAwait(false)
            : await admissions.PrepareContributionAsync(context.User, call.WorkspaceId, call.TaskId, call.Revision, input.TaskContentHash,
                input.AuthorityRevision, input.AuthorityFingerprint, context.RequestAborted).ConfigureAwait(false);
        if (prepared is null) return null;
        if (prepared.PolicyContextFingerprint != input.PolicyContextFingerprint)
            throw new AgentWorkspaceConflictException("The original import policy binding changed.");
        // A human may seal contribution under fresh independent checks, but only
        // the actual current agent supplies its framework-authenticated full policy
        // context to canonical lease admission. Never reconstruct executor claims.
        if (authorized.ContributorActorId != authorized.Task.OwnerActorId)
            return await ReadAsync(context, reference).ConfigureAwait(false);
        var executor = context.User;
        if (!AgentCredentialAuthority.IsAgent(executor) || CanonicalSecurityActor.Resolve(executor)?.ActorId != prepared.ExecutorActorId)
            throw new UnauthorizedAccessException("The actual original agent must submit the prepared import.");
        var intent = DatasetImportSubmissionIntent.Create(authorized.Task, prepared, source);
        await intent.RequireCurrentCallerAsync(services, context.User, context.RequestAborted).ConfigureAwait(false);
        var queue = services.GetRequiredService<IImportJobService>() as DurableImportJobService
            ?? throw new AgentWorkspaceUnavailableException();
        var jobId = await queue.QueueDatasetImportAsync(authorized.Task, prepared, source, context.User, executor, context.RequestAborted).ConfigureAwait(false);
        if (jobId != prepared.OperationId) throw new InvalidOperationException("The accepted job changed the original prepared operation.");
        // Even a successful queue return is not fabricated into a client receipt.
        // Read the actual authoritative job and independently authorize its observation.
        return await ReadAsync(context, reference).ConfigureAwait(false);
    }

    private async Task<BoundSubmission?> LoadAsync(HttpContext context, ReadDatasetImportAuthority request)
    {
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null || !await AgentCredentialAuthority.IsCurrentObserverAsync(context.User, context.RequestServices, context.RequestAborted).ConfigureAwait(false)) return null;
        var document = await store.GetAsync(request.WorkspaceId).ConfigureAwait(false);
        var target = await store.GetTargetIdAsync().ConfigureAwait(false);
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (document is null || !AgentWorkspaceState.CanRead(document, actor, tenant, target) ||
            !document.DatasetImportTasks.TryGetValue(request.TaskId, out var versions) ||
            versions.SingleOrDefault(task => task.Revision == request.Revision) is not { } task || task.ContentHash != request.ContentHash ||
            DatasetImportTaskState.ContentHash(task) != request.ContentHash || task.WorkspaceId != request.WorkspaceId ||
            task.TargetId != target || task.TenantId != tenant ||
            !document.DatasetImportExecutionBindings.TryGetValue(task.TaskId, out var binding) ||
            binding.TaskId != task.TaskId || binding.TaskRevision != task.Revision || binding.TaskContentHash != task.ContentHash ||
            binding.ExecutorActorId != task.OwnerActorId ||
            (actor != task.OwnerActorId && (actor != binding.InitiatingHumanActorId || document.OwnerId != actor)) ||
            !document.Turns.TryGetValue(binding.OriginatingTurnId, out var turn) ||
            (binding.InitiatingHumanActorId is { } human && (turn.ActorId != human || document.OwnerId != human)) ||
            binding.OriginalTurnHash != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)))) return null;
        document.DatasetImportPreparedAdmissions.TryGetValue(task.TaskId, out var prepared);
        if (prepared is not null && (prepared.TaskId != task.TaskId || prepared.TaskRevision != task.Revision || prepared.TaskContentHash != task.ContentHash ||
            prepared.ExecutorActorId != task.OwnerActorId || prepared.InitiatingHumanActorId != binding.InitiatingHumanActorId ||
            prepared.OriginatingTurnId != binding.OriginatingTurnId || prepared.OriginalTurnHash != binding.OriginalTurnHash ||
            prepared.AcceptBefore != task.UploadAdmissionDeadline || prepared.AuthorityRevision != binding.AuthorityRevision ||
            prepared.AuthorityFingerprint != binding.AuthorityFingerprint || prepared.PolicyContextFingerprint != binding.PolicyContextFingerprint)) return null;
        if (!await AgentCredentialAuthority.IsCurrentObserverAsync(context.User, context.RequestServices, context.RequestAborted).ConfigureAwait(false)) return null;
        return new(task, binding, prepared);
    }

    private async Task<bool> CurrentAsync(HttpContext context, ReadDatasetImportAuthority request, BoundSubmission original)
    {
        var latest = await LoadAsync(context, request).ConfigureAwait(false);
        return latest is not null && latest.Task == original.Task &&
            Same(latest.Binding, original.Binding, AgentWorkspaceJsonContext.Default.DatasetImportExecutionBinding) &&
            Same(latest.Prepared, original.Prepared, AgentWorkspaceJsonContext.Default.DatasetImportPreparedAdmission);
    }

    private static void RequireAccepted(BoundSubmission original, ExecutionJobRecord job, JobExecutionLeaseObservation observed)
    {
        var prepared = original.Prepared ?? throw new UnauthorizedAccessException();
        var expected = new JobExecutionLeaseAdmissionBinding
        {
            WorkspaceId = original.Task.WorkspaceId,
            TaskId = original.Task.TaskId,
            TaskRevision = original.Task.Revision,
            TaskContentHash = original.Task.ContentHash,
            AuthorityRevision = prepared.AuthorityRevision,
            AuthorityFingerprint = prepared.AuthorityFingerprint,
            PolicyContextFingerprint = prepared.PolicyContextFingerprint,
            InitiatingHumanActorId = prepared.InitiatingHumanActorId,
            OriginatingTurnId = prepared.OriginatingTurnId,
            OriginalTurnHash = prepared.OriginalTurnHash
        };
        if (!Enum.IsDefined(job.Status) || job.Spec.Kind != ExecutionJobKind.FileImport || job.OperationId != prepared.OperationId ||
            job.AcceptanceDeadline != prepared.AcceptBefore || job.Audit.RequestedBy != prepared.ExecutorActorId || job.Audit.TenantId != original.Task.TenantId ||
            job.Audit.SubmitterSecurityContext?.ExecutionLease is not { } lease ||
            observed.JobId != job.OperationId || observed.LeaseId != lease.LeaseId || observed.Admission != expected ||
            observed.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(job) || observed.ExecutorActorId != prepared.ExecutorActorId ||
            observed.InitiatingHumanActorId != prepared.InitiatingHumanActorId || observed.TenantId != original.Task.TenantId)
            throw new UnauthorizedAccessException("The accepted receipt does not match the original sealed import.");
    }

    private static DatasetImportSubmissionReceipt Receipt(BoundSubmission value, DatasetImportJobReceipt? job = null)
        => new(value.Task.TaskId, value.Task.Revision, value.Task.ContentHash,
            job is not null ? "accepted" : value.Prepared is not null ? "prepared" : "not-prepared",
            value.Prepared?.OperationId, value.Prepared?.AuthorityRevision, value.Prepared?.AuthorityFingerprint,
            value.Prepared?.PolicyContextFingerprint, value.Prepared?.AcceptBefore, job);

    private static DatasetImportJobReceipt ProjectJob(ExecutionJobRecord job, JobExecutionLeaseObservation observed)
        => new(job.OperationId, observed.LeaseId, job.Status.ToString().ToLowerInvariant(), job.CreatedAt, job.UpdatedAt, job.CompletedAt,
            job.PercentComplete is { } percent && double.IsFinite(percent) && percent is >= 0 and <= 100 ? percent : null,
            job.Status == ExecutionJobStatus.Succeeded ? job.FileImportOutput : null,
            job.Status == ExecutionJobStatus.Failed ? "import-failed" : null,
            job.Status is ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running
                && JobExecutionLeaseObservationEligibility.AllowsRetainedFact(job, JobExecutionLeaseObservationKind.Status));

    private static void RequireReviewed(DatasetImportSubmissionReceipt value, SubmitDatasetImport input)
    {
        if (value.AuthorityRevision != input.AuthorityRevision || value.AuthorityFingerprint != input.AuthorityFingerprint ||
            value.PolicyContextFingerprint != input.PolicyContextFingerprint)
            throw new AgentWorkspaceConflictException("The original submission has a different sealed authority.");
    }

    private static void Validate(ReadDatasetImportAuthority value)
    {
        if (!DatasetImportTaskState.ValidAuthorityScope(value.WorkspaceId, value.TaskId, value.Revision, value.ContentHash))
            throw new ArgumentException("An immutable original dataset task binding is required.");
    }
    private static ImportUploadTaskReference Reference(ReadDatasetImportAuthority value) => new(value.WorkspaceId, value.TaskId, value.Revision, value.ContentHash);
    private static ImportUploadTaskAuthority Authority(HttpContext context) => new(context.RequestServices.GetRequiredService<IDatasetImportTaskProvider>(),
        context.RequestServices.GetService<IDatasetImportContributorAuthorizer>());
    private static bool Same<T>(T? left, T? right, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
        => JsonSerializer.Serialize(left, type) == JsonSerializer.Serialize(right, type);
    private sealed record BoundSubmission(DatasetImportTask Task, DatasetImportExecutionBinding Binding, DatasetImportPreparedAdmission? Prepared);
}
