// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Inert until a host explicitly supplies canonical policy and complete kind-specific
/// scope adapters. No unknown kind, missing approval evidence or store failure falls back.
/// </summary>
internal sealed class JobExecutionLeaseAuthority(
    IExecutionJobStore jobs,
    IJobExecutionLeasePolicy policy,
    IEnumerable<IJobExecutionLeaseScopeProvider> providers,
    IDataProtectionProvider protection,
    TimeProvider clock,
    IServiceProvider services) : IJobExecutionLeaseAuthority
{
    private readonly IServiceProvider _services = services;
    private readonly IExecutionJobStore _jobs = jobs;
    private readonly JobExecutionLeaseProtector _protector = new(protection, clock);
    private readonly IJobExecutionLeaseScopeProvider[] _providers = providers.ToArray();
    private readonly IServiceScopeFactory _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

    public async Task<JobExecutionLease> IssueAsync(JobExecutionLeaseIssuanceRequest request, CancellationToken cancellationToken = default)
    {
        var job = request.PreparedJob;
        var original = job.Audit.SubmitterSecurityContext ?? throw Denied();
        var originalIntent = JobExecutionLeaseIntentFingerprint.Compute(job);
        if (original.AgentCredential is null || original.ExecutionLease is not null
            || job.CancellationRequestedAt is not null || job.Status != ExecutionJobStatus.Queued)
        {
            throw Denied();
        }
        var captured = await AgentJobSecurityBridge.CaptureAsync(request.Executor,
            _services.GetService<IOptions<RbacOptions>>()?.Value ?? new RbacOptions(), _services,
            _services.GetService<ITenantContext>(), cancellationToken).ConfigureAwait(false);
        if (JobSecurityContextFingerprint.ComputeRenewalLineage(captured) != JobSecurityContextFingerprint.ComputeRenewalLineage(original)
            || JobExecutionLeaseIntentFingerprint.ComputeContext(original with { AgentCredential = captured.AgentCredential })
                != JobExecutionLeaseIntentFingerprint.ComputeContext(original))
        {
            throw Denied();
        }
        var provider = Provider(job.Spec.Kind);
        var scope = CopyScope(await provider.PrepareAsync(job, request.Executor, cancellationToken).ConfigureAwait(false));
        var decision = await policy.AuthorizeIssuanceAsync(request, CopyScope(scope), cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw Denied();
        }
        var current = await AgentJobSecurityBridge.RevalidateAsync(original, _services, cancellationToken).ConfigureAwait(false);
        await provider.RequireCurrentAsync(job, CopyScope(scope), current, current.CurrentAgentPrincipal!, null, cancellationToken).ConfigureAwait(false);
        var hardDeadline = await AgentCredentialAuthority.LeaseSourceDeadlineAsync(original.AgentCredential, _services, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var expiresAt = new[] { request.RequestedExpiresAt, decision.MaximumExpiresAt,
            now.AddHours(24), now.Add((job.TimeoutPolicy ?? JobTimeoutPolicy.Default).MaxDuration), hardDeadline ?? DateTimeOffset.MaxValue }.Min();
        cancellationToken.ThrowIfCancellationRequested();
        if (originalIntent != JobExecutionLeaseIntentFingerprint.Compute(job))
        {
            throw Denied();
        }
        return _protector.ProtectValidatedBinding(job, scope, decision, request.Admission, expiresAt);
    }

    public async Task<JobSecurityContext> RevalidateForExecutionAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
    {
        var live = await ValidateBaseAsync(job.OperationId, job, execution: true, cancellationToken).ConfigureAwait(false);
        var context = BindTransient(live, execution: true);
        using (JobSecurityScope.BeginExecution(live.Job, context))
        {
            await Provider(live.Job.Spec.Kind).RequireCurrentAsync(live.Job, CopyScope(live.Binding.Scope), context,
                context.CurrentAgentPrincipal!, null, cancellationToken).ConfigureAwait(false);
            _ = await ValidateBaseAsync(job.OperationId, job, execution: true, cancellationToken).ConfigureAwait(false);
        }
        return context;
    }

    public async Task RequireCurrentLineageAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
    {
        _ = await ValidateBaseAsync(job.OperationId, job, execution: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobExecutionLeaseObservation> ResolveForObservationAsync(string jobId, ClaimsPrincipal observer,
        JobExecutionLeaseObservationKind kind, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind) || !await AgentCredentialAuthority.IsCurrentObserverAsync(observer, _services, cancellationToken).ConfigureAwait(false))
        {
            throw Denied();
        }
        var live = await ValidateBaseAsync(jobId, null, execution: false, cancellationToken).ConfigureAwait(false);
        var original = live.Job.Audit.SubmitterSecurityContext!;
        if (JobSecurityContextCapture.ResolveTenantId(observer, _services.GetService<ITenantContext>()) != original.TenantId)
        {
            throw Denied();
        }
        var request = new OperatorAuthorizationRequest { ResourceType = OperatorResourceType.Job, ResourceId = jobId, Operation = OperatorOperation.Read };
        var scopes = _services.GetRequiredService<IOperatorScopeAuthorizer>();
        var evaluator = _services.GetRequiredService<IOperatorAuthorizationEvaluator>();
        if (!scopes.Evaluate(observer, request.ResourceType, request.Operation).IsAllowed
            || !(await evaluator.EvaluateAsync(observer, request, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            throw Denied();
        }
        var context = BindTransient(live, execution: false);
        using (JobSecurityScope.BeginExecution(live.Job, context))
        {
            var provider = Provider(live.Job.Spec.Kind);
            await provider.RequireCurrentAsync(live.Job, CopyScope(live.Binding.Scope), context, context.CurrentAgentPrincipal!, null, cancellationToken).ConfigureAwait(false);
            await provider.RequireCurrentAsync(live.Job, CopyScope(live.Binding.Scope), context, observer, kind, cancellationToken).ConfigureAwait(false);
            _ = await ValidateBaseAsync(jobId, live.Job, execution: false, cancellationToken).ConfigureAwait(false);
            if (!await AgentCredentialAuthority.IsCurrentObserverAsync(observer, _services, cancellationToken).ConfigureAwait(false))
            {
                throw Denied();
            }
        }
        return new()
        {
            JobId = jobId, LeaseId = live.Binding.LeaseId, IntentFingerprint = live.Binding.IntentFingerprint,
            ExecutorActorId = original.OwnerActorId!, TenantId = original.TenantId, ExpiresAt = live.Binding.ExpiresAt,
            InitiatingHumanActorId = live.Binding.Admission?.InitiatingHumanActorId, Admission = live.Binding.Admission
        };
    }

    private JobSecurityContext BindTransient(LiveLease live, bool execution)
    {
        var original = live.Job.Audit.SubmitterSecurityContext!;
        var scopeFactory = _scopeFactory;
        var principal = JobExecutionLeaseTransientAuthority.Bind(live.Principal, _protector, live.Binding, live.Job, original, clock,
            async cancellationToken =>
            {
                await using var refreshScope = scopeFactory.CreateAsyncScope();
                var fresh = refreshScope.ServiceProvider.GetRequiredService<JobExecutionLeaseAuthority>();
                _ = await fresh.ValidateBaseAsync(live.Job.OperationId, live.Job, execution, cancellationToken).ConfigureAwait(false);
                return true;
            });
        return original with { CurrentAgentPrincipal = principal, CredentialExpiresAt = live.Binding.ExpiresAt };
    }

    private async Task<LiveLease> ValidateBaseAsync(string jobId, ExecutionJobRecord? expected,
        bool execution, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        if (job.OperationId != jobId || (execution && (job.CancellationRequestedAt is not null
            || job.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running))))
        {
            throw Denied();
        }
        var binding = _protector.UnprotectAndMatchAuthoritativeJob(job);
        if (expected is not null && (JobExecutionLeaseIntentFingerprint.Compute(expected) != binding.IntentFingerprint
            || expected.Audit.SubmitterSecurityContext?.ExecutionLease != job.Audit.SubmitterSecurityContext?.ExecutionLease))
        {
            throw Denied();
        }
        var principal = await AgentCredentialAuthority.RevalidateLeaseLineageAsync(_protector, binding, job, _services, cancellationToken).ConfigureAwait(false)
            ?? throw Denied();
        var context = job.Audit.SubmitterSecurityContext!;
        if (CanonicalSecurityActor.Resolve(principal)?.ActorId != context.OwnerActorId
            || context.PrincipalId != context.OwnerActorId || !AgentCredentialAuthority.MatchesTenant(principal, context.TenantId))
        {
            throw Denied();
        }
        using (var currentPolicy = new CurrentPolicyEvaluation(this, job, binding, execution))
        {
            await policy.RequireCurrentAsync(job, binding.Decision, CopyScope(binding.Scope), currentPolicy, cancellationToken).ConfigureAwait(false);
        }
        // Retained approval evidence can await external stores. Refresh credential lineage
        // once more after that wait and check the finite proof deadline before returning.
        var finalJob = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        RequireMatchingCurrentJob(finalJob, job, binding, execution);
        principal = await AgentCredentialAuthority.RevalidateLeaseLineageAsync(_protector, binding, finalJob, _services, cancellationToken).ConfigureAwait(false)
            ?? throw Denied();
        finalJob = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        RequireMatchingCurrentJob(finalJob, job, binding, execution);
        _protector.RequireVerifiedCurrentBinding(binding, finalJob);
        cancellationToken.ThrowIfCancellationRequested();
        return new(finalJob, binding, principal);
    }

    private static void RequireMatchingCurrentJob(ExecutionJobRecord actual, ExecutionJobRecord original,
        SealedJobExecutionLeaseBinding binding, bool execution)
    {
        if (actual.OperationId != binding.JobId
            || actual.Audit.SubmitterSecurityContext?.ExecutionLease != original.Audit.SubmitterSecurityContext?.ExecutionLease
            || JobExecutionLeaseIntentFingerprint.Compute(actual) != binding.IntentFingerprint
            || (execution && (actual.CancellationRequestedAt is not null
                || actual.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running))))
        {
            throw Denied();
        }
    }

    private static JobExecutionLeaseScope CopyScope(JobExecutionLeaseScope scope)
        => scope with
        {
            Resources = scope.Resources.Select(resource => resource with { }).ToArray(),
            Operations = scope.Operations.Select(operation => operation with
                { ApprovalReasonCodes = operation.ApprovalReasonCodes.ToArray() }).ToArray()
        };

    private sealed class CurrentPolicyEvaluation(JobExecutionLeaseAuthority owner, ExecutionJobRecord job,
        SealedJobExecutionLeaseBinding binding, bool execution) : IJobExecutionLeaseCurrentPolicy, IDisposable
    {
        private bool _disposed;
        public void Dispose() => _disposed = true;

        public async Task<JobExecutionLeaseCurrentPolicyDecision> EvaluateAsync(OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var exact = binding.Scope.Operations.FirstOrDefault(operation => SameRequest(operation, request)) ?? throw Denied();
            var principal = await RequireSourceAsync(cancellationToken).ConfigureAwait(false);
            var scopeDecision = owner._services.GetRequiredService<IOperatorScopeAuthorizer>()
                .Evaluate(principal, exact.ResourceType, exact.Operation);
            var authorization = scopeDecision.IsAllowed
                ? await owner._services.GetRequiredService<IOperatorAuthorizationEvaluator>()
                    .EvaluateAsync(principal, exact with { ApprovalReasonCodes = exact.ApprovalReasonCodes.ToArray() }, cancellationToken).ConfigureAwait(false)
                : Honua.Core.Features.Security.Domain.AccessDecision.Forbidden(scopeDecision.Reason);
            var requirement = authorization.IsAllowed
                ? owner._services.GetRequiredService<IOperatorApprovalEvaluator>().Evaluate(principal,
                    exact with { ApprovalReasonCodes = exact.ApprovalReasonCodes.ToArray() })
                : ApprovalRequirement.NotRequired();
            _ = await RequireSourceAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return new() { Authorization = authorization,
                Approval = requirement with { ReasonCodes = requirement.ReasonCodes.ToArray() } };
        }

        private async Task<ClaimsPrincipal> RequireSourceAsync(CancellationToken cancellationToken)
        {
            // Deliberately no ValidateBaseAsync: that would recurse into the policy adapter.
            var actual = await owner._jobs.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
            RequireMatchingCurrentJob(actual, job, binding, execution);
            owner._protector.RequireVerifiedCurrentBinding(binding, actual);
            var principal = await AgentCredentialAuthority.RevalidateLeaseLineageAsync(owner._protector, binding, actual,
                owner._services, cancellationToken).ConfigureAwait(false) ?? throw Denied();
            actual = await owner._jobs.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
            RequireMatchingCurrentJob(actual, job, binding, execution);
            owner._protector.RequireVerifiedCurrentBinding(binding, actual);
            cancellationToken.ThrowIfCancellationRequested();
            return principal;
        }

        private static bool SameRequest(OperatorAuthorizationRequest left, OperatorAuthorizationRequest right)
            => left.ResourceType == right.ResourceType && left.ResourceId == right.ResourceId && left.Operation == right.Operation
                && left.WorkspaceVisibility == right.WorkspaceVisibility && left.WorkspaceOwnerId == right.WorkspaceOwnerId
                && left.WorkspaceScopeId == right.WorkspaceScopeId && left.IsDestructive == right.IsDestructive
                && left.RequiresExplicitApproval == right.RequiresExplicitApproval && left.ApprovalPolicyRef == right.ApprovalPolicyRef
                && left.ApprovalReasonCodes.SequenceEqual(right.ApprovalReasonCodes, StringComparer.Ordinal);
    }

    private IJobExecutionLeaseScopeProvider Provider(ExecutionJobKind kind)
    {
        var matches = _providers.Where(provider => provider.Kind == kind).ToArray();
        return matches.Length == 1 ? matches[0] : throw Denied();
    }

    private static UnauthorizedAccessException Denied() => new("The job execution authority is unavailable or no longer current.");
    private sealed record LiveLease(ExecutionJobRecord Job, SealedJobExecutionLeaseBinding Binding, ClaimsPrincipal Principal);
}
