// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class JobExecutionLeaseAuthorityTests
{
    [Theory]
    [InlineData("cancel", true)]
    [InlineData("terminal", true)]
    [InlineData("cancel", false)]
    [InlineData("terminal", false)]
    public async Task Execution_RechecksActualStatusDuringAndAfterCurrentPolicy(string transition, bool insideCallback)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Stored = fixture.Accept(await fixture.Authority.IssueAsync(fixture.Request));
        var expected = fixture.Stored;
        fixture.Policy.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(),
            Arg.Any<JobExecutionLeaseScope>(), Arg.Any<IJobExecutionLeaseCurrentPolicy>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                fixture.Stored = transition == "cancel"
                    ? fixture.Stored! with { CancellationRequestedAt = DateTimeOffset.UtcNow }
                    : fixture.Stored! with { Status = ExecutionJobStatus.Succeeded };
                if (insideCallback)
                {
                    await call.ArgAt<IJobExecutionLeaseCurrentPolicy>(3).EvaluateAsync(call.ArgAt<JobExecutionLeaseScope>(2).Operations[0]);
                }
            });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionClaim_PropagatesThroughNestedScopesAndRejectsReclaimedAttempt(bool replaceWorker)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Stored = fixture.Accept(await fixture.Authority.IssueAsync(fixture.Request)) with
            { Status = ExecutionJobStatus.Running, ClaimedBy = "worker", AttemptCount = 1 };
        var expected = fixture.Stored;
        var current = await fixture.Authority.RevalidateForExecutionAsync(expected);
        using var execution = JobSecurityScope.BeginExecution(expected, current);
        Assert.Equal("worker", JobSecurityScope.Current!.WorkerId);
        Assert.Equal(1, JobSecurityScope.Current.AttemptCount);
        using (JobSecurityScope.Begin(current))
        {
            Assert.Equal("worker", JobSecurityScope.Current!.WorkerId);
            Assert.Equal(1, JobSecurityScope.Current.AttemptCount);
        }
        fixture.Stored = replaceWorker ? fixture.Stored with { ClaimedBy = "replacement" }
            : fixture.Stored with { AttemptCount = 2 };
        Assert.False(await AgentCredentialAuthority.IsCurrentAsync(current.CurrentAgentPrincipal!, fixture.Services));
        using (JobSecurityScope.BeginExecution(fixture.Stored, current))
        {
            Assert.False(JobExecutionLeaseTransientAuthority.MatchesExecutionClaim(current.CurrentAgentPrincipal!,
                fixture.Stored.ClaimedBy!, fixture.Stored.AttemptCount));
            Assert.False(await AgentCredentialAuthority.IsCurrentAsync(current.CurrentAgentPrincipal!, fixture.Services));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(expected));
        var fresh = await fixture.Authority.RevalidateForExecutionAsync(fixture.Stored);
        using (JobSecurityScope.BeginExecution(fixture.Stored, fresh))
            Assert.True(JobExecutionLeaseTransientAuthority.MatchesExecutionClaim(fresh.CurrentAgentPrincipal!,
                fixture.Stored.ClaimedBy!, fixture.Stored.AttemptCount));
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("cleared")]
    [InlineData("disposition")]
    public async Task PendingCommitMetadata_AfterExpiryIsReadOnlyAndPinsReconciliationState(string change)
    {
        var clock = new ObservationClock();
        using var fixture = await Fixture.CreateAsync(clock, ExecutionJobKind.FileImport);
        var human = await fixture.Keys.CreateAsync("observer", ["admin:read"], null, null, default);
        var observer = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(human.Record);
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        var accepted = fixture.Accept(lease);
        fixture.Stored = accepted with { Status = ExecutionJobStatus.Queued,
            CommitRecovery = new(JobExecutionLeaseIntentFingerprint.Compute(accepted), "worker", 1, clock.GetUtcNow()) };
        fixture.Services.GetRequiredService<IOperatorScopeAuthorizer>()
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>())
            .Returns(OperatorScopeDecision.Allowed());
        fixture.Services.GetRequiredService<IOperatorAuthorizationEvaluator>()
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        clock.UtcNow = lease.ExpiresAt.AddSeconds(1);
        fixture.Services.GetRequiredService<IJobExecutionLeaseObservationPolicy>()
            .RequireObservationAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(), Arg.Any<JobExecutionLeaseScope>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<JobExecutionLeaseObservationKind>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (change == "cleared") fixture.Stored = fixture.Stored! with { CommitRecovery = null };
                if (change == "disposition") fixture.Stored = fixture.Stored! with
                    { CommitRecovery = fixture.Stored.CommitRecovery! with { RetryAllowed = false } };
                return Task.CompletedTask;
            });
        if (change != "unchanged")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.ResolveForObservationAsync(accepted.OperationId,
                observer, JobExecutionLeaseObservationKind.Status));
            return;
        }
        var observed = await fixture.Authority.ResolveForObservationAsync(accepted.OperationId, observer, JobExecutionLeaseObservationKind.Status);
        Assert.Equal(lease.LeaseId, observed.LeaseId);
        Assert.Null(JobSecurityScope.Current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.ResolveForObservationAsync(accepted.OperationId,
            observer, JobExecutionLeaseObservationKind.OutputContent));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(fixture.Stored));
    }

    [Fact]
    public async Task HistoricalObservation_AfterExecutionExpiryRequiresCurrentIndependentObserverAndExecutorLineage()
    {
        var clock = new ObservationClock();
        using var fixture = await Fixture.CreateAsync(clock);
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        fixture.Stored = fixture.Accept(lease) with { Status = ExecutionJobStatus.Succeeded };
        var human = await fixture.Keys.CreateAsync("observer", ["admin:read"], null, null, default);
        var observer = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(human.Record);
        fixture.Services.GetRequiredService<IOperatorScopeAuthorizer>()
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>())
            .Returns(OperatorScopeDecision.Allowed());
        fixture.Services.GetRequiredService<IOperatorAuthorizationEvaluator>()
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        clock.UtcNow = lease.ExpiresAt.AddSeconds(1);
        var observation = await fixture.Authority.ResolveForObservationAsync(fixture.Stored.OperationId, observer, JobExecutionLeaseObservationKind.Status);
        Assert.Equal(lease.LeaseId, observation.LeaseId);
        Assert.Equal(lease.ExpiresAt, observation.ExpiresAt);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(fixture.Stored));
        await fixture.Keys.RevokeAsync(fixture.KeyId, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.ResolveForObservationAsync(fixture.Stored.OperationId,
            observer, JobExecutionLeaseObservationKind.Status));
        Assert.Equal(ExecutionJobStatus.Succeeded, fixture.Stored.Status);
    }

    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(true, "output")]
    [InlineData(true, "status")]
    [InlineData(true, "artifacts")]
    [InlineData(true, "committed")]
    [InlineData(true, "observer-revoked")]
    public async Task HistoricalInitiatingHuman_UsesOwnCurrentAuthorityAfterExecutorRevocation(bool executionExpired, string change)
    {
        var clock = new ObservationClock();
        using var fixture = await Fixture.CreateAsync(clock);
        var human = await fixture.Keys.CreateAsync("initiator", ["admin:read"], null, null, default);
        var observer = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(human.Record);
        var humanActor = CanonicalSecurityActor.Resolve(observer)!.ActorId;
        var request = fixture.Request with { Admission = new()
        {
            WorkspaceId = "workspace", TaskId = "task", TaskRevision = 1, TaskContentHash = new string('a', 64),
            AuthorityRevision = 1, AuthorityFingerprint = new string('b', 64), PolicyContextFingerprint = new string('c', 64),
            InitiatingHumanActorId = humanActor, OriginatingTurnId = "turn", OriginalTurnHash = new string('d', 64)
        } };
        var lease = await fixture.Authority.IssueAsync(request);
        fixture.Stored = fixture.Accept(lease) with { Status = ExecutionJobStatus.Succeeded };
        fixture.Services.GetRequiredService<IOperatorScopeAuthorizer>()
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>())
            .Returns(OperatorScopeDecision.Allowed());
        fixture.Services.GetRequiredService<IOperatorAuthorizationEvaluator>()
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        if (executionExpired) clock.UtcNow = lease.ExpiresAt.AddSeconds(1);
        await fixture.Keys.RevokeAsync(fixture.KeyId, default);
        fixture.Services.GetRequiredService<IJobExecutionLeaseObservationPolicy>()
            .RequireObservationAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(), Arg.Any<JobExecutionLeaseScope>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<JobExecutionLeaseObservationKind>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                fixture.Stored = change switch
                {
                    "output" => fixture.Stored with { FileImportOutput = new() { Schema = "other", PhysicalTableName = "other" } },
                    "status" => fixture.Stored with { Status = ExecutionJobStatus.Failed },
                    "artifacts" => fixture.Stored with { ArtifactReferences = ["replacement"] },
                    "committed" => fixture.Stored with { CommittedEffectReferences = ["replacement"] },
                    _ => fixture.Stored
                };
                if (change == "observer-revoked") await fixture.Keys.RevokeAsync(human.Record.Id, default);
            });
        if (change != "unchanged")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.ResolveForObservationAsync(fixture.Stored.OperationId,
                observer, JobExecutionLeaseObservationKind.Status));
            return;
        }
        var observed = await fixture.Authority.ResolveForObservationAsync(fixture.Stored.OperationId, observer, JobExecutionLeaseObservationKind.Status);
        Assert.Equal(humanActor, observed.InitiatingHumanActorId);
        Assert.Equal(lease.LeaseId, observed.LeaseId);
        Assert.Null(JobSecurityScope.Current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(fixture.Stored));
        // Current observer authorization still applies; no protected executor principal escapes.
        fixture.Services.GetRequiredService<IOperatorAuthorizationEvaluator>()
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Forbidden("Current observer grant removed."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.ResolveForObservationAsync(fixture.Stored.OperationId,
            observer, JobExecutionLeaseObservationKind.Status));
        await fixture.Keys.RevokeAsync(human.Record.Id, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.ResolveForObservationAsync(fixture.Stored.OperationId,
            observer, JobExecutionLeaseObservationKind.Status));
        Assert.Equal(ExecutionJobStatus.Succeeded, fixture.Stored.Status);
    }

    private sealed class ObservationClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Fact]
    public async Task CurrentPolicy_IsExactFreshAndDetectsNewApprovalRequirement()
    {
        using var fixture = await Fixture.CreateAsync();
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        fixture.Stored = fixture.Accept(lease);
        fixture.Services.GetRequiredService<IOperatorScopeAuthorizer>()
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>())
            .Returns(OperatorScopeDecision.NotGoverned());
        var evaluator = fixture.Services.GetRequiredService<IOperatorAuthorizationEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Honua.Core.Features.Security.Domain.AccessDecision.Allowed());
        var approvals = fixture.Services.GetRequiredService<IOperatorApprovalEvaluator>();
        approvals.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.Required("new-current-policy", "tightened"));
        IJobExecutionLeaseCurrentPolicy? retained = null;
        OperatorAuthorizationRequest? exact = null;
        fixture.Policy.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(),
            Arg.Any<JobExecutionLeaseScope>(), Arg.Any<IJobExecutionLeaseCurrentPolicy>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                retained = call.ArgAt<IJobExecutionLeaseCurrentPolicy>(3);
                exact = call.ArgAt<JobExecutionLeaseScope>(2).Operations[0];
                var decision = await retained.EvaluateAsync(exact);
                decision.Authorization.IsAllowed.Should().BeTrue();
                decision.Approval.PolicyRef.Should().Be("new-current-policy");
                var changed = () => retained.EvaluateAsync(exact with { ApprovalReasonCodes = ["different"] });
                await changed.Should().ThrowAsync<UnauthorizedAccessException>();
                throw new UnauthorizedAccessException("No retained approval matches tightened policy.");
            });
        var execute = () => fixture.Authority.RevalidateForExecutionAsync(fixture.Stored!);
        await execute.Should().ThrowAsync<UnauthorizedAccessException>();
        var expiredCallback = () => retained!.EvaluateAsync(exact!);
        await expiredCallback.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Issuance_RequiresActualAcceptanceAndRetainsUnchangedCredentialPositive()
    {
        using var fixture = await Fixture.CreateAsync();
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        var accepted = fixture.Accept(lease);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(accepted));
        fixture.Stored = accepted;
        var context = await fixture.Authority.RevalidateForExecutionAsync(accepted);
        using var scope = JobSecurityScope.BeginExecution(accepted, context);
        Assert.True(await AgentCredentialAuthority.IsCurrentAsync(context.CurrentAgentPrincipal!, fixture.Services, default));
        Assert.False(context.CurrentAgentPrincipal!.IsInRole("admin"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AgentJobSecurityBridge.CaptureAsync(context.CurrentAgentPrincipal,
            new RbacOptions(), fixture.Services, null, default));
        await fixture.Keys.RotateAsync(fixture.KeyId, default);
        Assert.False(await AgentCredentialAuthority.IsCurrentAsync(context.CurrentAgentPrincipal, fixture.Services, default));
    }

    [Theory]
    [InlineData("rotated")]
    [InlineData("revoked")]
    [InlineData("cancelled")]
    [InlineData("intent")]
    [InlineData("policy")]
    [InlineData("missing")]
    public async Task CurrentAuthority_RejectsChangedStoredJobOrExecutor(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        var accepted = fixture.Accept(lease);
        fixture.Stored = accepted;
        switch (change)
        {
            case "rotated": await fixture.Keys.RotateAsync(fixture.KeyId, default); break;
            case "revoked": await fixture.Keys.RevokeAsync(fixture.KeyId, default); break;
            case "cancelled": fixture.Stored = accepted with { CancellationRequestedAt = DateTimeOffset.UtcNow }; break;
            case "intent": fixture.Stored = accepted with { Spec = accepted.Spec with { Backend = "different" } }; break;
            case "policy": fixture.Policy.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<IJobExecutionLeaseCurrentPolicy>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException(new UnauthorizedAccessException("Approval withdrawn."))); break;
            case "missing": fixture.Stored = null; break;
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(accepted));
    }

    [Fact]
    public async Task Issuance_RefusesChangedExecutorOrPolicyWithoutMandatoryExtraApprovalForBenignJobs()
    {
        using var fixture = await Fixture.CreateAsync();
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        Assert.NotNull(lease);
        Assert.Null(fixture.Request.Admission);
        await fixture.Keys.RotateAsync(fixture.KeyId, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.IssueAsync(fixture.Request));
    }

    [Fact]
    public async Task Issuance_RefusesMissingScopeAdapterAndDeniedCanonicalPolicy()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Provider.Kind.Returns(ExecutionJobKind.FileImport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.IssueAsync(fixture.Request));
        fixture.Provider.Kind.Returns(ExecutionJobKind.Geoprocessing);
        fixture.Policy.AuthorizeIssuanceAsync(Arg.Any<JobExecutionLeaseIssuanceRequest>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<CancellationToken>())
            .Returns(new JobExecutionLeasePolicyDecision { IsAllowed = false, DecisionId = "required-approval-absent", MaximumExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.IssueAsync(fixture.Request));
    }

    [Fact]
    public async Task TerminalLineage_DoesNotGrantExecutionOrEraseTruthfulCompletedState()
    {
        using var fixture = await Fixture.CreateAsync();
        var lease = await fixture.Authority.IssueAsync(fixture.Request);
        var accepted = fixture.Accept(lease) with { Status = ExecutionJobStatus.Succeeded };
        fixture.Stored = accepted;
        await fixture.Authority.RequireCurrentLineageAsync(accepted);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RevalidateForExecutionAsync(accepted));
        Assert.Equal(ExecutionJobStatus.Succeeded, fixture.Stored.Status);
        await fixture.Keys.RevokeAsync(fixture.KeyId, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Authority.RequireCurrentLineageAsync(accepted));
        Assert.Equal(ExecutionJobStatus.Succeeded, fixture.Stored.Status);
    }

    [Fact]
    public async Task SingletonFacade_RefreshesThroughNewScopesAfterOriginalPolicyGraphIsDisposed()
    {
        var tracking = new List<TrackingPolicy>();
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("worker", ["agent:workspace", "admin:write"], null, null, default);
        var store = Substitute.For<IExecutionJobStore>();
        ExecutionJobRecord? stored = null;
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(stored));
        var provider = Substitute.For<IJobExecutionLeaseScopeProvider, IJobExecutionLeaseObservationScopeProvider>();
        provider.Kind.Returns(ExecutionJobKind.Geoprocessing);
        provider.PrepareAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new JobExecutionLeaseScope
        {
            Kind = ExecutionJobKind.Geoprocessing, Resources = [], ProviderBinding = "constant-plan",
            Operations = [new() { ResourceType = OperatorResourceType.Process, Operation = OperatorOperation.Execute }]
        });
        provider.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<JobSecurityContext>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<JobExecutionLeaseObservationKind?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var services = new ServiceCollection()
            .AddSingleton<IAdminApiKeyStore>(keys).AddSingleton<IExecutionJobStore>(store)
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .AddSingleton(provider)
            .AddScoped<IJobExecutionLeasePolicy>(_ => { var policy = new TrackingPolicy(); tracking.Add(policy); return policy; })
            .AddScoped<JobExecutionLeaseAuthority>()
            .AddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var context = await AgentJobSecurityBridge.CaptureAsync(principal, new RbacOptions(), services, null, default);
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "scoped-job", Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
            Spec = new() { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", Kind = ExecutionJobKind.Geoprocessing, WorkloadName = "constant" },
            Audit = new() { RequestedBy = context.OwnerActorId, TenantId = context.TenantId, SubmitterSecurityContext = context }
        };
        var authority = services.GetRequiredService<IJobExecutionLeaseAuthority>();
        var lease = await authority.IssueAsync(new() { PreparedJob = job, Executor = principal, RequestedExpiresAt = now.AddMinutes(30) });
        stored = job with { Audit = job.Audit with { SubmitterSecurityContext = context with { ExecutionLease = lease } } };
        var current = await authority.RevalidateForExecutionAsync(stored);
        Assert.All(tracking, policy => Assert.True(policy.Disposed));
        var originalScopes = tracking.Count;
        using (JobSecurityScope.BeginExecution(stored, current))
        {
            Assert.True(await AgentCredentialAuthority.IsCurrentAsync(current.CurrentAgentPrincipal!, services, default));
        }
        Assert.True(tracking.Count > originalScopes);
        Assert.All(tracking, policy => Assert.True(policy.Disposed));
    }

    private sealed class TrackingPolicy : IJobExecutionLeasePolicy, IDisposable
    {
        internal bool Disposed { get; private set; }
        public Task<JobExecutionLeasePolicyDecision> AuthorizeIssuanceAsync(JobExecutionLeaseIssuanceRequest request,
            JobExecutionLeaseScope scope, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return Task.FromResult(new JobExecutionLeasePolicyDecision
                { IsAllowed = true, DecisionId = "benign", MaximumExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        }
        public Task RequireCurrentAsync(ExecutionJobRecord job, JobExecutionLeasePolicyDecision decision, JobExecutionLeaseScope scope, IJobExecutionLeaseCurrentPolicy currentPolicy, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return Task.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture : IDisposable
    {
        internal required ServiceProvider Services { get; init; }
        internal required InMemoryAdminApiKeyStore Keys { get; init; }
        internal required Guid KeyId { get; init; }
        internal required IJobExecutionLeasePolicy Policy { get; init; }
        internal required IJobExecutionLeaseScopeProvider Provider { get; init; }
        internal required JobExecutionLeaseAuthority Authority { get; init; }
        internal required JobExecutionLeaseIssuanceRequest Request { get; init; }
        internal ExecutionJobRecord? Stored { get; set; }

        internal ExecutionJobRecord Accept(JobExecutionLease lease)
            => Request.PreparedJob with { Audit = Request.PreparedJob.Audit with
                { SubmitterSecurityContext = Request.PreparedJob.Audit.SubmitterSecurityContext! with { ExecutionLease = lease } } };

        internal static async Task<Fixture> CreateAsync(TimeProvider? clock = null, ExecutionJobKind kind = ExecutionJobKind.Geoprocessing)
        {
            clock ??= TimeProvider.System;
            var keys = new InMemoryAdminApiKeyStore();
            var created = await keys.CreateAsync("worker", ["agent:workspace", "admin:write"], null, null, default);
            JobExecutionLeaseAuthority? currentAuthority = null;
            var services = new ServiceCollection().AddSingleton<JobExecutionLeaseAuthority>(_ => currentAuthority
                    ?? throw new InvalidOperationException("Fixture authority is not initialized."))
                .AddSingleton<IAdminApiKeyStore>(keys)
                .AddSingleton(Substitute.For<IOperatorAuthorizationEvaluator>())
                .AddSingleton(Substitute.For<IOperatorApprovalEvaluator>())
                .AddSingleton(Substitute.For<IOperatorScopeAuthorizer>())
                .AddSingleton(Substitute.For<IJobExecutionLeaseObservationPolicy>())
                .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions())).BuildServiceProvider();
            var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
            var context = await AgentJobSecurityBridge.CaptureAsync(principal, new RbacOptions(), services, null, default);
            var now = clock.GetUtcNow();
            var job = new ExecutionJobRecord
            {
                OperationId = "job", Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
                Spec = new() { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", Kind = kind, WorkloadName = "constant" },
                Audit = new() { RequestedBy = context.OwnerActorId, TenantId = context.TenantId, SubmitterSecurityContext = context }
            };
            var scope = new JobExecutionLeaseScope
            {
                Kind = job.Spec.Kind, Resources = [], ProviderBinding = "server-verified-constant-plan",
                Operations = [new() { ResourceType = OperatorResourceType.Process, ResourceId = "constant", Operation = OperatorOperation.Execute }]
            };
            var policy = Substitute.For<IJobExecutionLeasePolicy>();
            policy.AuthorizeIssuanceAsync(Arg.Any<JobExecutionLeaseIssuanceRequest>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<CancellationToken>())
                .Returns(new JobExecutionLeasePolicyDecision { IsAllowed = true, DecisionId = "canonical-benign", MaximumExpiresAt = now.AddHours(1) });
            policy.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<IJobExecutionLeaseCurrentPolicy>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var provider = Substitute.For<IJobExecutionLeaseScopeProvider, IJobExecutionLeaseObservationScopeProvider>();
            provider.Kind.Returns(job.Spec.Kind);
            provider.PrepareAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(scope);
            provider.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<JobSecurityContext>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<JobExecutionLeaseObservationKind?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var store = Substitute.For<IExecutionJobStore>();
            var authority = new JobExecutionLeaseAuthority(store, policy, [provider], new EphemeralDataProtectionProvider(), clock, services);
            currentAuthority = authority;
            var fixture = new Fixture
            {
                Services = services, Keys = keys, KeyId = created.Record.Id, Policy = policy, Provider = provider, Authority = authority,
                Request = new() { PreparedJob = job, Executor = principal, RequestedExpiresAt = now.AddMinutes(30) }
            };
            store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(fixture.Stored));
            return fixture;
        }

        public void Dispose() => Services.Dispose();
    }
}
