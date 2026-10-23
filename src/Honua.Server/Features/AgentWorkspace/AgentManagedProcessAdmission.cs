// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.AgentWorkspace;

// This adapter grants exactly one explicitly bounded inline source contract. It
// does not infer authority from generic analytic/managed catalog classification.
internal sealed class AgentManagedProcessAdmission(IAgentWorkspaceStore workspaces,
    IHttpContextAccessor accessor, IConfiguration configuration, IServiceProvider services, TimeProvider clock)
    : IAgentManagedProcessAdmission, IJobExecutionLeaseScopeProvider, IJobExecutionLeaseObservationScopeProvider
{
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;
    private GeoprocessingExecutorOptions Options => services.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>().CurrentValue;

    public Task<JobSecurityContext> CaptureAsync(AnalysisPlan plan, ClaimsPrincipal executor, CancellationToken cancellationToken = default)
        => RequestBoundaryAsync(() => CaptureCoreAsync(plan, executor, cancellationToken));

    private async Task<JobSecurityContext> CaptureCoreAsync(AnalysisPlan plan, ClaimsPrincipal executor, CancellationToken cancellationToken)
    {
        AgentManagedBufferContract.RequirePlan(plan, Options);
        var http = RequireRequest(executor);
        var turn = await AgentStudioAuthorization.BindAsync(http, executor, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        var context = await services.GetRequiredService<IAgentJobSecurityContextCapture>().CaptureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var original = await LoadTurnAsync(turn.WorkspaceId, turn.TurnId, context.OwnerActorId!, context.TenantId!, cancellationToken).ConfigureAwait(false);
        await RequireProcessAsync(executor, cancellationToken).ConfigureAwait(false);
        _ = await SourcesAsync(original.Turn, context, executor, cancellationToken).ConfigureAwait(false);
        if (!await AgentCredentialAuthority.IsCurrentAsync(executor, services, cancellationToken).ConfigureAwait(false)) throw Denied();
        return context;
    }

    public Task<ExecutionJobRecord> SealAsync(ExecutionJobRecord job, AnalysisPlan plan, ClaimsPrincipal executor, CancellationToken cancellationToken = default)
        => RequestBoundaryAsync(() => SealCoreAsync(job, plan, executor, cancellationToken));

    private async Task<ExecutionJobRecord> SealCoreAsync(ExecutionJobRecord job, AnalysisPlan plan, ClaimsPrincipal executor,
        CancellationToken cancellationToken)
    {
        AgentManagedBufferContract.RequirePlan(plan, Options);
        AgentManagedBufferContract.RequireJob(job, Options);
        var http = RequireRequest(executor);
        var turn = await AgentStudioAuthorization.BindAsync(http, executor, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        var context = job.Audit.SubmitterSecurityContext ?? throw Denied();
        if (job.Status != ExecutionJobStatus.Queued || context.ExecutionLease is not null || context.AgentCredential is null
            || job.Spec.AgentManagedProcessBinding is not null || job.Audit.RequestedBy != context.OwnerActorId
            || job.Audit.RequestFingerprint != GeoprocessingJobService.CreateRequestFingerprint(plan)) throw Denied();
        var captured = await services.GetRequiredService<IAgentJobSecurityContextCapture>().CaptureCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!FileImportExecutionLeaseScopeProvider.MatchesPreparedContext(captured, context)) throw Denied();
        var projected = GeoprocessingSpecBuilder.ProjectPlanParameters(plan, new(StringComparer.Ordinal));
        if (AgentManagedBufferContract.PlanParametersFingerprint(projected) != AgentManagedBufferContract.PlanParametersFingerprint(job.Spec.Parameters)) throw Denied();
        var original = await LoadTurnAsync(turn.WorkspaceId, turn.TurnId, context.OwnerActorId!, context.TenantId!, cancellationToken).ConfigureAwait(false);
        var binding = new AgentManagedProcessJobBinding
        {
            Contract = AgentManagedBufferContract.Format,
            WorkspaceId = turn.WorkspaceId,
            TurnId = turn.TurnId,
            TurnHash = HashTurn(original.Turn),
            TargetId = turn.TargetId,
            ReleaseId = turn.ReleaseId,
            WorkspaceOwnerActorId = original.Document.OwnerId,
            ExecutorActorId = context.OwnerActorId!,
            TenantId = context.TenantId!,
            PolicyContextFingerprint = JobExecutionLeaseIntentFingerprint.ComputeContext(context),
            PlanParametersFingerprint = AgentManagedBufferContract.PlanParametersFingerprint(job.Spec.Parameters),
            MaximumInputBytes = AgentManagedBufferContract.MaximumInputBytes,
            MaximumOutputBytes = AgentManagedBufferContract.MaximumOutputBytes,
            Sources = await SourcesAsync(original.Turn, context, executor, cancellationToken).ConfigureAwait(false)
        };
        var now = clock.GetUtcNow();
        job = job with
        {
            AcceptanceDeadline = now.AddSeconds(30),
            Spec = job.Spec with { AgentManagedProcessBinding = binding },
            Audit = job.Audit with { TenantId = context.TenantId, OperationInstanceId = job.OperationId },
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(Math.Min(300, Options.MaxLayerExecutionSeconds)) }
        };
        var lease = await services.GetRequiredService<IJobExecutionLeaseAuthority>().IssueAsync(new()
        {
            PreparedJob = job,
            Executor = executor,
            RequestedExpiresAt = now.Add(job.TimeoutPolicy.MaxDuration)
        }, cancellationToken).ConfigureAwait(false);
        return job with { Audit = job.Audit with { SubmitterSecurityContext = context with { ExecutionLease = lease } } };
    }

    public Task RequireReplayAsync(ExecutionJobRecord job, ClaimsPrincipal executor, CancellationToken cancellationToken = default)
        => RequestBoundaryAsync(async () => { await RequireReplayCoreAsync(job, executor, cancellationToken).ConfigureAwait(false); return true; });

    private async Task RequireReplayCoreAsync(ExecutionJobRecord job, ClaimsPrincipal executor, CancellationToken cancellationToken)
    {
        var http = RequireRequest(executor);
        var selected = await AgentStudioAuthorization.BindAsync(http, executor, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        var binding = await LoadJobAsync(job, cancellationToken).ConfigureAwait(false);
        if (selected.WorkspaceId != binding.WorkspaceId || selected.TurnId != binding.TurnId
            || selected.TargetId != binding.TargetId || selected.ReleaseId != binding.ReleaseId || selected.TenantId != binding.TenantId) throw Denied();
        await RequireAccessAsync(job, binding, executor, execute: true, cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<IJobExecutionLeaseAuthority>().RequireCurrentLineageAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobExecutionLeaseScope> PrepareAsync(ExecutionJobRecord job, ClaimsPrincipal executor, CancellationToken cancellationToken = default)
    {
        RequireRequest(executor);
        var binding = await LoadJobAsync(job, cancellationToken).ConfigureAwait(false);
        if (job.Status != ExecutionJobStatus.Queued || job.Audit.SubmitterSecurityContext?.ExecutionLease is not null
            || job.AcceptanceDeadline is not { } deadline || deadline <= clock.GetUtcNow()) throw Denied();
        await RequireAccessAsync(job, binding, executor, execute: true, cancellationToken).ConfigureAwait(false);
        return Scope(binding);
    }

    public async Task RequireCurrentAsync(ExecutionJobRecord job, JobExecutionLeaseScope scope, JobSecurityContext context,
        ClaimsPrincipal principal, JobExecutionLeaseObservationKind? kind, CancellationToken cancellationToken = default)
    {
        var binding = await LoadJobAsync(job, cancellationToken).ConfigureAwait(false);
        if (Serialize(Scope(binding)) != Serialize(scope)) throw Denied();
        if (job.Audit.SubmitterSecurityContext?.ExecutionLease is null)
        {
            if (kind is not null || job.Status != ExecutionJobStatus.Queued || job.AcceptanceDeadline is not { } deadline || deadline <= clock.GetUtcNow()
                || !ReferenceEquals(context.CurrentAgentPrincipal, principal)
                || !FileImportExecutionLeaseScopeProvider.MatchesPreparedContext(context, job.Audit.SubmitterSecurityContext!)) throw Denied();
        }
        else if (JobSecurityScope.Current is not { Submitter: { } active } execution || execution.JobId != job.OperationId
            || execution.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(job)
            || active.ExecutionLease != job.Audit.SubmitterSecurityContext.ExecutionLease
            || active.CurrentAgentPrincipal is null || !JobExecutionLeaseTransientAuthority.IsLeasePrincipal(active.CurrentAgentPrincipal)
            || JobExecutionLeaseIntentFingerprint.ComputeContext(context) != JobExecutionLeaseIntentFingerprint.ComputeContext(active)
            || kind is null && !ReferenceEquals(principal, active.CurrentAgentPrincipal)) throw Denied();
        if (kind is not null && !Enum.IsDefined(kind.Value)) throw Denied();
        await RequireAccessAsync(job, binding, principal, kind is null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequireObservationAsync(ExecutionJobRecord job, JobExecutionLeaseScope scope, ClaimsPrincipal observer,
        JobExecutionLeaseObservationKind kind, IJobExecutionLeaseProvenanceValidator provenance, CancellationToken cancellationToken = default)
    {
        var binding = await LoadJobAsync(job, cancellationToken).ConfigureAwait(false);
        if (!JobExecutionLeaseObservationEligibility.AllowsRetainedFact(job, kind) || Serialize(Scope(binding)) != Serialize(scope)) throw Denied();
        await RequireAccessAsync(job, binding, observer, execute: false, cancellationToken).ConfigureAwait(false);
        foreach (var resource in scope.Resources)
        {
            await provenance.RequireResourceAsync(resource, cancellationToken).ConfigureAwait(false);
            var source = binding.Sources.Single(item => item.ResourceId == resource.ResourceId && item.StorageLayerId == resource.StorageLayerId);
            if (await provenance.GetPolicyFingerprintAsync(resource, cancellationToken).ConfigureAwait(false) != source.PolicyFingerprint) throw Denied();
        }
        if (kind == JobExecutionLeaseObservationKind.OutputContent && (job.ArtifactReferences.Count == 0
            || job.ArtifactReferences.Any(reference => !reference.StartsWith("data:application/geo+json;base64,", StringComparison.Ordinal)))) throw Denied();
    }

    private async Task RequireAccessAsync(ExecutionJobRecord job, AgentManagedProcessJobBinding binding,
        ClaimsPrincipal principal, bool execute, CancellationToken cancellationToken)
    {
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId;
        if (actor is null || actor != binding.ExecutorActorId && (execute || actor != binding.WorkspaceOwnerActorId)
            || !await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false)) throw Denied();
        var original = await LoadTurnAsync(binding.WorkspaceId, binding.TurnId, binding.ExecutorActorId, binding.TenantId, cancellationToken).ConfigureAwait(false);
        if (!AgentWorkspaceState.CanRead(original.Document, actor, binding.TenantId, binding.TargetId)) throw Denied();
        if (execute) await RequireProcessAsync(principal, cancellationToken).ConfigureAwait(false);
        var current = await SourcesAsync(original.Turn, job.Audit.SubmitterSecurityContext!, principal, cancellationToken).ConfigureAwait(false);
        if (!current.SequenceEqual(binding.Sources)) throw Denied();
        _ = await LoadJobAsync(job, cancellationToken).ConfigureAwait(false);
        if (!await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false)) throw Denied();
    }

    private async Task<AgentManagedProcessJobBinding> LoadJobAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        AgentManagedBufferContract.RequireJob(job, Options);
        var binding = job.Spec.AgentManagedProcessBinding ?? throw Denied();
        if (binding.Contract != AgentManagedBufferContract.Format || binding.MaximumInputBytes != AgentManagedBufferContract.MaximumInputBytes
            || binding.MaximumOutputBytes != AgentManagedBufferContract.MaximumOutputBytes
            || binding.Sources is null || binding.Sources.Length is < 1 or > 8
            || binding.ReleaseId != configuration["AgentWorkspace:ReleaseId"]
            || job.Audit.SubmitterSecurityContext is not { AgentCredential: not null } context
            || context.PrincipalId != binding.ExecutorActorId || context.OwnerActorId != binding.ExecutorActorId || context.TenantId != binding.TenantId
            || job.Audit.RequestedBy != binding.ExecutorActorId || job.Audit.TenantId != binding.TenantId || job.Audit.OperationInstanceId != job.OperationId
            || JobExecutionLeaseIntentFingerprint.ComputeContext(context) != binding.PolicyContextFingerprint
            || AgentManagedBufferContract.PlanParametersFingerprint(job.Spec.Parameters) != binding.PlanParametersFingerprint) throw Denied();
        var original = await LoadTurnAsync(binding.WorkspaceId, binding.TurnId, binding.ExecutorActorId, binding.TenantId, cancellationToken).ConfigureAwait(false);
        if (original.Document.OwnerId != binding.WorkspaceOwnerActorId || original.Document.TargetId != binding.TargetId
            || HashTurn(original.Turn) != binding.TurnHash) throw Denied();
        return binding;
    }

    private async Task<(AgentWorkspaceDocument Document, AgentWorkspaceTurn Turn)> LoadTurnAsync(string workspaceId,
        string turnId, string executor, string tenant, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = await workspaces.GetAsync(workspaceId).ConfigureAwait(false) ?? throw Denied();
        if (string.IsNullOrWhiteSpace(tenant) || !AgentWorkspaceState.CanRead(document, executor, tenant,
                await workspaces.GetTargetIdAsync().ConfigureAwait(false)) || !document.Turns.TryGetValue(turnId, out var turn)
            || turn.Context.WorkspaceId != workspaceId || turn.Context.TargetId != document.TargetId || turn.Context.TenantId != tenant
            // Other task/source contexts need their own complete current authority
            // adapter. The first contract reads only selected published resources.
            || turn.Context.Task is not null || turn.LocalProcessSnapshot is not null || turn.BrowserFileSelection is not null
            || turn.ActorId != document.OwnerId || document.OwnerId != executor && document.AgentActorId != executor) throw Denied();
        return (document, turn);
    }

    private async Task<AgentManagedProcessSourceBinding[]> SourcesAsync(AgentWorkspaceTurn turn, JobSecurityContext context,
        ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (turn.Context.SelectedResources.Length is < 1 or > 8 || context.AgentCredential is null) throw Denied();
        if (JobExecutionLeaseTransientAuthority.IsLeasePrincipal(principal))
            await JobExecutionLeaseTenantContext.BindExecutionTenantAsync(services, principal, cancellationToken).ConfigureAwait(false);
        else JobExecutionLeaseTenantContext.BindRequestTenant(services, principal);
        var selectedTenant = services.GetRequiredService<ITenantContext>();
        if (selectedTenant is Honua.Infrastructure.MultiTenancy.RequestTenantContext { IsInitialized: false }
            || selectedTenant.TenantId != context.TenantId) throw Denied();
        var snapshot = await services.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<AgentManagedProcessSourceBinding>();
        foreach (var selected in turn.Context.SelectedResources)
        {
            if (selected.Kind != "layer" || selected.ServiceId is null
                || !int.TryParse(selected.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) throw Denied();
            var service = snapshot.Graph.Services.SingleOrDefault(item => item.Metadata.Id == selected.ServiceId) ?? throw Denied();
            var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, index) ?? throw Denied();
            var resource = snapshot.ResolveResource(publication) ?? throw Denied();
            var layerId = snapshot.ResolveStorageLayerId(publication) ?? throw Denied();
            if (!snapshot.IsRoutable(publication) || !MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, selectedTenant.TenantId)
                || !services.GetRequiredService<IOperatorScopeAuthorizer>().Evaluate(principal, OperatorResourceType.Catalog, OperatorOperation.Read).IsAllowed
                || !(await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(services, principal, selectedTenant.TenantId,
                    applyTenantScope: true, resource, service, AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false)).IsAllowed) throw Denied();
            IReadOnlyCollection<string> names = snapshot.Index.ServiceNamesByResource.TryGetValue(resource.Metadata.Id, out var publications) ? publications : Array.Empty<string>();
            var policyPrincipal = AgentJobSecurityBridge.CreatePolicyEvaluationPrincipal(context);
            var rows = services.GetRequiredService<IRowLevelSecurityFilterSource>() as RowLevelSecurityFilterSource ?? throw Denied();
            var masks = services.GetRequiredService<IFieldMaskSource>() as FieldMaskSource ?? throw Denied();
            var filter = await rows.ResolveForProvenanceAsync(resource, policyPrincipal, context.AgentCredential.MembershipRoles, names, cancellationToken).ConfigureAwait(false);
            var fields = await masks.ResolveForProvenanceAsync(resource, context.AgentCredential.MembershipRoles, names, cancellationToken).ConfigureAwait(false);
            var publicationBytes = JsonSerializer.SerializeToUtf8Bytes(publication, MetadataV2JsonContext.Default.MetadataV2Publication);
            var serviceBytes = JsonSerializer.SerializeToUtf8Bytes(service, MetadataV2JsonContext.Default.MetadataV2Service);
            if (publicationBytes.Length + serviceBytes.Length > 262144) throw Denied();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(serviceBytes);
            hash.AppendData(publicationBytes);
            result.Add(new(service.Metadata.Id, index, publication.Metadata.Id, resource.Metadata.Id, layerId,
                JobResourcePolicyFingerprint.Compute(resource, new ReadSecurityResolution(filter, fields)), Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        if (result.Select(source => (source.ServiceId, source.PublishedLayerIndex)).Distinct().Count() != result.Count
            || result.Select(source => source.StorageLayerId).Distinct().Count() != result.Count) throw Denied();
        return result.ToArray();
    }

    private async Task RequireProcessAsync(ClaimsPrincipal executor, CancellationToken cancellationToken)
    {
        var operation = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            ResourceId = AgentManagedProcessAuthorization.ResourceId,
            Operation = OperatorOperation.Execute
        };
        if (!services.GetRequiredService<IOperatorScopeAuthorizer>().Evaluate(executor, operation.ResourceType, operation.Operation).IsAllowed
            || !(await services.GetRequiredService<IOperatorAuthorizationEvaluator>().EvaluateAsync(executor, operation, cancellationToken).ConfigureAwait(false)).IsAllowed) throw Denied();
    }

    private HttpContext RequireRequest(ClaimsPrincipal executor)
    {
        var http = accessor.HttpContext;
        if (http is null || !ReferenceEquals(http.User, executor) || !AgentCredentialAuthority.IsAgent(executor)
            || !executor.HasClaim("permission", AgentManagedProcessAuthorization.Permission)) throw Denied();
        return http;
    }

    private static JobExecutionLeaseScope Scope(AgentManagedProcessJobBinding binding) => new()
    {
        Kind = ExecutionJobKind.Geoprocessing,
        Resources = binding.Sources.Select(source => new JobExecutionLeaseResource
        {
            ResourceType = OperatorResourceType.Catalog,
            ResourceId = source.ResourceId,
            StorageLayerId = source.StorageLayerId,
            Operation = AuthorizationOperation.Query
        }).Distinct().ToArray(),
        Operations = [new() { ResourceType = OperatorResourceType.Process, ResourceId = AgentManagedProcessAuthorization.ResourceId, Operation = OperatorOperation.Execute }],
        ProviderBinding = JsonSerializer.Serialize(binding, AgentManagedProcessJsonContext.Default.AgentManagedProcessJobBinding)
    };

    private static string HashTurn(AgentWorkspaceTurn turn)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
    private static string Serialize(JobExecutionLeaseScope scope) => JsonSerializer.Serialize(scope, AgentManagedProcessJsonContext.Default.JobExecutionLeaseScope);
    private static async Task<T> RequestBoundaryAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (UnauthorizedAccessException)
        {
            throw new GeoprocessingAuthorizationException(false,
                "The exact bounded process grant and original live workspace/source authority are required.",
                OperatorResourceType.Process, OperatorOperation.Execute);
        }
    }
    private static UnauthorizedAccessException Denied() => AgentManagedBufferContract.Denied();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentManagedProcessJobBinding))]
[JsonSerializable(typeof(JobExecutionLeaseScope))]
internal sealed partial class AgentManagedProcessJsonContext : JsonSerializerContext;
