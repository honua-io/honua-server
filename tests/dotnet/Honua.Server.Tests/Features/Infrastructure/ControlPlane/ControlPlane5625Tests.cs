// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Features.Admin.Routing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Unit coverage for the control-plane findings in honua-server#5625 that are
/// exercised without a database: principal capture, rollback CAS, proposal
/// source-status rejection, topology recovery, and promotion idempotency.
/// </summary>
public sealed class ControlPlane5625Tests
{
    [UnitTest]
    public async Task CreateAsync_CapturesRequesterTenantAndRoles()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        WorkflowOperationRecord? saved = null;
        store.TryCreateAsync(
                Arg.Do<WorkflowOperationRecord>(operation => saved = operation),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var service = new CoordinatedReleaseControlService([store]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "subject-1"),
                new Claim(ClaimTypes.Name, "Ada"),
                new Claim("tenant_id", "tenant-a"),
                new Claim(ClaimTypes.Role, "admin"),
            ],
            authenticationType: "Bearer",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

        var created = await service.CreateAsync(
            Container(),
            Plan(),
            "prod",
            "requester",
            "reason",
            "idem-1",
            "corr-1",
            requester: principal,
            tenantOptions: new TenantContextOptions());

        saved.Should().NotBeNull();
        saved!.Audit.TenantId.Should().Be("tenant-a");
        saved.Audit.RequestedBy.Should().Be("requester");
        saved.CoordinatedRelease!.RequesterTenantId.Should().Be("tenant-a");
        saved.CoordinatedRelease.RequesterSubject.Should().Be("subject-1");
        saved.CoordinatedRelease.RequesterRoles.Should().Contain("admin");
        created.OperationId.Should().Be(saved.OperationId);

        var restored = CoordinatedReleasePrincipal.Restore(saved.CoordinatedRelease);
        restored.Identity!.IsAuthenticated.Should().BeTrue();
        restored.IsInRole("admin").Should().BeTrue();
        restored.HasClaim("tenant_id", "tenant-a").Should().BeTrue();
        restored.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("subject-1");
    }

    [UnitTest]
    public void Restore_EmptySnapshot_IsUnauthenticated()
    {
        var restored = CoordinatedReleasePrincipal.Restore(Context());

        restored.Identity!.IsAuthenticated.Should().BeFalse();
        restored.Claims.Should().BeEmpty();
        restored.IsInRole("admin").Should().BeFalse();
    }

    [UnitTest]
    public async Task ContainerStep_PassesRestoredPrincipalAndTenantToChildDeploy()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        WorkflowOperationRecord? child = null;
        store.TryCreateAsync(
                Arg.Do<WorkflowOperationRecord>(operation => child = operation),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var registry = Substitute.For<IDeployTargetRegistry>();
        registry.GetAsync("target-1", Arg.Any<CancellationToken>()).Returns(new DeployTargetDefinition
        {
            TargetId = "target-1",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "local",
            Environment = "prod",
            TargetName = "api",
            RequiresApproval = false,
        });
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("local");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new DeployBackendCapabilities());
        backend.PlanAsync(Arg.Any<DeployOperationSpec>(), Arg.Any<CancellationToken>())
            .Returns(new DeployPlan { IsReadyToSubmit = true, RequiresApproval = false });
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted });
        var evaluator = new RecordingApprovalEvaluator();
        var deploy = new DeployWorkflowService(
            registry,
            [store],
            [backend],
            evaluator,
            NullLogger<DeployWorkflowService>.Instance,
            tenantOptions: Options.Create(new TenantContextOptions()));
        var step = new CoordinatedContainerStepExecutor(
            deploy,
            Substitute.For<IWorkflowOperationReconciler>(),
            [store]);
        var context = CoordinatedReleasePrincipal.Capture(
            Context(),
            new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "subject-1"),
                    new Claim("tenant_id", "tenant-a"),
                    new Claim(ClaimTypes.Role, "platform_admin"),
                ],
                "Bearer",
                ClaimTypes.Name,
                ClaimTypes.Role)),
            new TenantContextOptions());

        await step.StartAsync(context, Operation(context));

        evaluator.Principal.Should().NotBeNull();
        evaluator.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        evaluator.Principal.HasClaim("tenant_id", "tenant-a").Should().BeTrue();
        child.Should().NotBeNull();
        child!.Kind.Should().Be(WorkflowOperationKind.Deploy);
        child.Audit.TenantId.Should().Be("tenant-a");
    }

    [UnitTest]
    public async Task ApproveGate_PreservesRequester_AndRecordsApprover()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        var operation = Operation(Context()) with
        {
            Status = WorkflowOperationStatus.AwaitingApproval,
            Audit = new OperationAuditInfo { RequestedBy = "requester", Reason = "create" },
            CoordinatedRelease = Context() with { CurrentStep = CoordinatedReleaseStep.ContainerRollout },
        };
        store.GetAsync(operation.OperationId, Arg.Any<CancellationToken>()).Returns(operation);
        WorkflowOperationRecord? written = null;
        store.TrySetAsync(
                Arg.Do<WorkflowOperationRecord>(updated => written = updated),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var service = new CoordinatedReleaseControlService([store]);

        var approved = await service.ApproveGateAsync(
            operation.OperationId,
            CoordinatedReleaseStep.ContainerRollout,
            "approver",
            "ship it");

        approved!.Audit.RequestedBy.Should().Be("requester");
        approved.CoordinatedRelease!.GateApprovedBy.Should().Be("approver");
        approved.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        written!.Audit.RequestedBy.Should().Be("requester");
    }

    [UnitTest]
    public async Task RequestRollback_WhenAlreadyRequested_DoesNotWrite()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        var operation = Operation(Context() with
        {
            CurrentStep = CoordinatedReleaseStep.RollingBack,
            RollbackRequestedBy = "first",
        }) with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            Audit = new OperationAuditInfo { RequestedBy = "requester" },
        };
        store.GetAsync(operation.OperationId, Arg.Any<CancellationToken>()).Returns(operation);
        var service = new CoordinatedReleaseControlService([store]);

        var result = await service.RequestRollbackAsync(operation.OperationId, "second", "again");

        result!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        result.Audit.RequestedBy.Should().Be("requester");
        result.CoordinatedRelease!.RollbackRequestedBy.Should().Be("first");
        await store.DidNotReceive().TrySetAsync(
            Arg.Any<WorkflowOperationRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RequestRollback_CompareAndSwapMiss_ThrowsWithoutRewritingRequester()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        var operation = Operation(Context()) with
        {
            Status = WorkflowOperationStatus.Succeeded,
            Audit = new OperationAuditInfo { RequestedBy = "requester" },
        };
        store.GetAsync(operation.OperationId, Arg.Any<CancellationToken>()).Returns(operation);
        store.TrySetAsync(
                Arg.Any<WorkflowOperationRecord>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        var service = new CoordinatedReleaseControlService([store]);

        var act = () => service.RequestRollbackAsync(operation.OperationId, "operator", "revert");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*changed while rollback*");
        await store.Received(1).TrySetAsync(
            Arg.Is<WorkflowOperationRecord>(updated =>
                updated.Audit.RequestedBy == "requester"
                && updated.CoordinatedRelease!.RollbackRequestedBy == "operator"
                && updated.Status == WorkflowOperationStatus.RollbackRequested),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RejectProposal_ExecutingClaim_ThrowsAndDoesNotNotify()
    {
        var store = Substitute.For<IOperationProposalStore>();
        var awaiting = new OperationProposal
        {
            ProposalId = "proposal-1",
            Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.AwaitingApproval,
            Version = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        var executing = awaiting with
        {
            Status = OperationProposalStatus.Executing,
            Version = 2,
        };
        store.GetAsync("proposal-1", Arg.Any<CancellationToken>()).Returns(awaiting, executing);
        store.TrySetAsync(Arg.Any<OperationProposal>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var notifier = Substitute.For<IProposalNotifier>();
        var gateway = new OperationGateway(
            Substitute.For<IGuardrailLadder>(),
            store,
            Array.Empty<IOperationExecutor>(),
            Substitute.For<IServiceScopeFactory>(),
            notifier,
            NullLogger<OperationGateway>.Instance);

        var act = () => gateway.RejectProposalAsync("proposal-1", "approver-b", "too late");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("AwaitingApproval");
        await store.Received(1).TrySetAsync(
            Arg.Any<OperationProposal>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        await notifier.DidNotReceive().NotifyResolvedAsync(Arg.Any<OperationProposal>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void PromotionRequestBinding_LegacyAndHashReuse()
    {
        var legacy = Promotion(NetworkTopologyPromotionKind.Promote, to: 4, from: 3, hash: null);
        var act = () => NetworkTopologyPromotionRequestBinding.EnsureCompatible(
            legacy,
            NetworkTopologyPromotionKind.Promote,
            targetGeneration: 4,
            expectedActiveGeneration: 3,
            expectedActiveRowVersion: 9);
        act.Should().NotThrow();

        var kindMismatch = () => NetworkTopologyPromotionRequestBinding.EnsureCompatible(
            legacy,
            NetworkTopologyPromotionKind.Rollback,
            targetGeneration: 4,
            expectedActiveGeneration: 3,
            expectedActiveRowVersion: 9);
        kindMismatch.Should().Throw<NetworkTopologyPromotionConflictException>()
            .Which.Reason.Should().Be(NetworkTopologyPromotionRejection.IdempotencyKeyReused);

        var hash = NetworkTopologyPromotionRequestBinding.Compute(
            NetworkTopologyPromotionKind.Rollback, 2, 4, 9);
        var bound = Promotion(NetworkTopologyPromotionKind.Rollback, to: 2, from: 4, hash);
        var same = () => NetworkTopologyPromotionRequestBinding.EnsureCompatible(
            bound,
            NetworkTopologyPromotionKind.Rollback,
            2,
            4,
            9);
        same.Should().NotThrow();

        var rowMismatch = () => NetworkTopologyPromotionRequestBinding.EnsureCompatible(
            bound,
            NetworkTopologyPromotionKind.Rollback,
            2,
            4,
            10);
        rowMismatch.Should().Throw<NetworkTopologyPromotionConflictException>()
            .Which.Reason.Should().Be(NetworkTopologyPromotionRejection.IdempotencyKeyReused);
    }

    [UnitTest]
    public async Task TopologySubmission_CreateFailure_AbandonsUnclaimedAttempt()
    {
        var rebuild = Substitute.For<INetworkTopologyRebuildStore>();
        rebuild.CreateAttemptAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Attempt(owner: null));
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("redis down"));
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IExecutionJobStore)).Returns(jobs);
        var submission = new NetworkTopologyRebuildSubmissionService(
            rebuild,
            services,
            NullLogger<NetworkTopologyRebuildSubmissionService>.Instance);

        var act = () => submission.SubmitAsync("ds", 4, 1, 1, 4326);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("redis down");
        await rebuild.Received(1).TryAbandonUnclaimedAttemptAsync(
            "ds",
            4,
            1,
            "routing.topology.rebuild_abandoned",
            CancellationToken.None);
    }

    [UnitTest]
    public async Task TopologySubmission_CreatedJob_DoesNotAbandon()
    {
        var rebuild = Substitute.For<INetworkTopologyRebuildStore>();
        rebuild.CreateAttemptAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Attempt(owner: null));
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IExecutionJobStore)).Returns(jobs);
        var submission = new NetworkTopologyRebuildSubmissionService(
            rebuild,
            services,
            NullLogger<NetworkTopologyRebuildSubmissionService>.Instance);

        var result = await submission.SubmitAsync("ds", 4, 1, 1, 4326);

        result.Attempt.Should().Be(1);
        await rebuild.DidNotReceive().TryAbandonUnclaimedAttemptAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TopologyReconciler_UnclaimedMissingJob_AbandonsInsteadOfFailingLease()
    {
        var rebuild = Substitute.For<INetworkTopologyRebuildStore>();
        rebuild.ListExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Attempt(owner: null) });
        rebuild.TryAbandonUnclaimedAttemptAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);
        var reconciler = new NetworkTopologyRebuildReconciler(
            rebuild,
            jobs,
            NullLogger<NetworkTopologyRebuildReconciler>.Instance);

        var adopted = await reconciler.ReconcileAsync();

        adopted.Should().Be(1);
        await rebuild.Received(1).TryAbandonUnclaimedAttemptAsync(
            "ds",
            4,
            1,
            "routing.topology.rebuild_abandoned",
            CancellationToken.None);
        await rebuild.DidNotReceive().TryFailAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TopologyReconciler_ClaimedCancelledJob_FailsWithFencingToken()
    {
        var rebuild = Substitute.For<INetworkTopologyRebuildStore>();
        rebuild.ListExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Attempt(owner: "worker", token: 7) });
        rebuild.TryFailAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Job(ExecutionJobStatus.Cancelled));
        var reconciler = new NetworkTopologyRebuildReconciler(
            rebuild,
            jobs,
            NullLogger<NetworkTopologyRebuildReconciler>.Instance);

        var adopted = await reconciler.ReconcileAsync();

        adopted.Should().Be(1);
        await rebuild.Received(1).TryFailAsync(
            "ds",
            4,
            1,
            7,
            "routing.topology.rebuild_orphaned",
            Arg.Any<CancellationToken>());
        await rebuild.DidNotReceive().TryAbandonUnclaimedAttemptAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TopologyTerminalCallback_CancelledUnclaimedAttempt_Abandons()
    {
        var rebuild = Substitute.For<INetworkTopologyRebuildStore>();
        rebuild.GetAttemptAsync("ds", 4, 1, CancellationToken.None).Returns(Attempt(owner: null));
        var callback = Callback(rebuild);

        await callback.OnTerminalAsync(Job(ExecutionJobStatus.Cancelled), CancellationToken.None);

        await rebuild.Received(1).TryAbandonUnclaimedAttemptAsync(
            "ds",
            4,
            1,
            "routing.topology.rebuild_abandoned",
            CancellationToken.None);
    }

    [UnitTest]
    public async Task TopologyTerminalCallback_ClaimedOrSucceeded_DoesNotAbandon()
    {
        var claimed = Substitute.For<INetworkTopologyRebuildStore>();
        claimed.GetAttemptAsync("ds", 4, 1, CancellationToken.None).Returns(Attempt(owner: "worker", token: 3));
        await Callback(claimed).OnTerminalAsync(Job(ExecutionJobStatus.Failed), CancellationToken.None);
        await claimed.DidNotReceive().TryAbandonUnclaimedAttemptAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        var succeeded = Substitute.For<INetworkTopologyRebuildStore>();
        await Callback(succeeded).OnTerminalAsync(Job(ExecutionJobStatus.Succeeded), CancellationToken.None);
        await succeeded.DidNotReceive().GetAttemptAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
    }

    private static NetworkTopologyRebuildJobTerminalCallback Callback(INetworkTopologyRebuildStore store)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(INetworkTopologyRebuildStore)).Returns(store);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(scope);
        return new NetworkTopologyRebuildJobTerminalCallback(
            scopes,
            NullLogger<NetworkTopologyRebuildJobTerminalCallback>.Instance);
    }

    private static ExecutionJobRecord Job(ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new ExecutionJobRecord
        {
            OperationId = "op-1",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = NetworkTopologyRebuildExecutionSpecBuilder.Build(
                new NetworkTopologyRebuildJobRequest("ds", 4, 1, 1, 4326)),
        };
    }

    private static NetworkTopologyRebuildAttempt Attempt(string? owner, long token = 0) => new(
        "ds",
        4,
        1,
        NetworkTopologyRebuildAttemptState.Building,
        "op-1",
        1,
        1,
        null,
        null,
        null,
        null,
        owner,
        token,
        DateTimeOffset.UnixEpoch,
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        null);

    private static NetworkTopologyPromotionRecord Promotion(
        NetworkTopologyPromotionKind kind,
        long to,
        long from,
        string? hash) => new(
        "promo",
        "ds",
        from,
        to,
        kind,
        "actor",
        null,
        "idem",
        null,
        DateTimeOffset.UnixEpoch,
        hash);

    private static CoordinatedReleaseContainerSpec Container() => new()
    {
        TargetId = "target-1",
        DesiredImage = "honua:1",
        RequiresExplicitApproval = true,
    };

    private static MetadataReleaseExecutionPlan Plan() => new()
    {
        PackageId = "pkg",
        TargetEnvironment = "prod",
        ResourceSemanticId = "layer",
        NewFieldName = "note",
        Script = new MetadataReleaseScript
        {
            ScriptId = "script-1",
            ForwardOperations =
            [
                new MetadataReleaseScriptOperation
                {
                    Kind = MetadataReleaseScriptOperationKind.AddColumn,
                    ResourceSemanticId = "layer",
                    FieldName = "note",
                },
            ],
        },
    };

    private static CoordinatedReleaseContext Context() => new()
    {
        PackageId = "pkg",
        TargetEnvironment = "prod",
        Container = Container(),
        MetadataPlan = Plan(),
    };

    private static WorkflowOperationRecord Operation(CoordinatedReleaseContext context)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new WorkflowOperationRecord
        {
            OperationId = "coord-1",
            Kind = WorkflowOperationKind.CoordinatedRelease,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            Audit = new OperationAuditInfo { RequestedBy = "requester", CorrelationId = "corr-1" },
            CoordinatedRelease = context,
        };
    }

    private sealed class RecordingApprovalEvaluator : IOperatorApprovalEvaluator
    {
        public ClaimsPrincipal? Principal { get; private set; }

        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        {
            Principal = principal;
            return ApprovalRequirement.NotRequired();
        }
    }
}
