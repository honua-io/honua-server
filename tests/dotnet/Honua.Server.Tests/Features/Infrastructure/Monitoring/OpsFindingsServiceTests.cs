// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.ControlPlane;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit tests for the deterministic ops-findings rule set (<see cref="OpsFindingsService"/>), one
/// rule at a time against fake seams.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class OpsFindingsServiceTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_AlertDispatchDeadLetters_ProducesCriticalFindingWithNoAction()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 3, DeadLetteredCount = 2 },
        };
        var service = CreateService(alertHealth: alertHealth);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Equal("alert-dispatch", finding.Subject.Channel);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_AlertDispatchBelowThresholds_ProducesNoFinding()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 1, DeadLetteredCount = 0 },
        };
        var service = CreateService(alertHealth: alertHealth);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PlatformReleaseSkew_ProducesInformationalWarningWithNoAction()
    {
        var controlPlane = new ControlPlaneOptions
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.0",
                ServingArtifactReference = "registry/honua-serving:2026.07.0",
            },
            DeployTargets =
            [
                new DeployTargetOptions { TargetId = "serving-a", ArtifactReference = "registry/honua-serving:OLD" },
            ],
        };
        var service = CreateService(controlPlaneOptions: controlPlane);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RulePlatformReleaseSkew);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Equal("2026.07.0", finding.Subject.ReleaseVersion);
        Assert.Contains(finding.EvidenceRefs, e => e.Contains("serving-a", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PendingContractMigrations_ProducesWarningWithNoAction()
    {
        var probe = new FakeDeployPreflightProbe(BuildDeploySnapshot(
            hasPendingContractScripts: true,
            pendingContractScripts: ["V42__contract_drop_legacy.sql"]));
        var service = CreateService(deployProbe: probe);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RulePendingContractMigrations);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Contains(finding.EvidenceRefs, e => e.Contains("V42__contract_drop_legacy.sql", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_GpQueueDepthAboveThreshold_ProducesWarningWithNoAction()
    {
        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExecutionJobRecord>
            {
                BuildJob("j1", ExecutionJobStatus.Queued, "aws-batch"),
                BuildJob("j2", ExecutionJobStatus.Running, "aws-batch"),
                BuildJob("j3", ExecutionJobStatus.Provisioning, "k8s"),
            });
        var options = new OpsFindingsOptions { GpQueueDepthThreshold = 2 };
        var service = CreateService(jobStore: jobStore, options: options);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleGpQueueDepth);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Contains("3", finding.Explanation, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_DeployManualInterventionWithPriorRevision_ProducesCriticalFindingWithRollbackAction()
    {
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.ListActiveAsync(Arg.Any<WorkflowOperationKind?>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkflowOperationRecord>
            {
                BuildDeployOperation("op-1", WorkflowOperationStatus.ManualInterventionRequired, currentRevision: "rev-9", desiredRevision: "rev-10"),
            });
        var service = CreateService(workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleDeployManualIntervention);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.Equal("op-1", finding.Subject.OperationId);
        Assert.NotNull(finding.RecommendedAction);
        Assert.Equal(OperationClass.Deploy, finding.RecommendedAction!.Kind);
        // The rollback payload must target the prior revision (rev-9), not the failing desired revision.
        Assert.Contains("rev-9", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
        Assert.Contains("\"targetId\":\"target-op-1\"", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_DeployManualInterventionWithoutPriorRevision_ProducesInformationalFinding()
    {
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.ListActiveAsync(Arg.Any<WorkflowOperationKind?>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkflowOperationRecord>
            {
                BuildDeployOperation("op-2", WorkflowOperationStatus.ManualInterventionRequired, currentRevision: null, desiredRevision: "rev-10"),
            });
        var service = CreateService(workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleDeployManualIntervention);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.Null(finding.RecommendedAction);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_HealthyInstance_ProducesNoFindings()
    {
        var service = CreateService();

        var findings = await service.EvaluateAsync();

        Assert.Empty(findings);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_FindingIds_AreDeterministicAcrossEvaluations()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 1, DeadLetteredCount = 5 },
        };
        var service = CreateService(alertHealth: alertHealth);

        var first = await service.EvaluateAsync();
        var second = await service.EvaluateAsync();

        Assert.Equal(
            first.Single(f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog).Id,
            second.Single(f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog).Id);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_UnknownFinding_ReturnsFindingNotFound()
    {
        var service = CreateService();

        var result = await service.ProposeAsync("does-not-exist");

        Assert.Equal(OpsFindingProposalStatus.FindingNotFound, result.Status);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_InformationalFinding_ReturnsNoRecommendedAction()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 1, DeadLetteredCount = 5 },
        };
        var service = CreateService(alertHealth: alertHealth);
        var finding = (await service.EvaluateAsync()).Single(f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.NoRecommendedAction, result.Status);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_ActionableFinding_RoutesThroughGatewayKeyedOnFindingId()
    {
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.ListActiveAsync(Arg.Any<WorkflowOperationKind?>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkflowOperationRecord>
            {
                BuildDeployOperation("op-7", WorkflowOperationStatus.ManualInterventionRequired, currentRevision: "rev-1", desiredRevision: "rev-2"),
            });

        var gateway = Substitute.For<IOperationGateway>();
        gateway.RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.ProposalCreated,
                Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"),
                ProposalId = "proposal-42",
            });

        var service = CreateService(workflowStore: workflowStore, gateway: gateway);
        var finding = (await service.EvaluateAsync()).Single(f => f.Rule == OpsFindingsService.RuleDeployManualIntervention);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, result.Status);
        Assert.Equal("proposal-42", result.ProposalId);
        await gateway.Received(1).RouteAsync(
            Arg.Is<OperationGatewayRequest>(r =>
                r.Kind == OperationClass.Deploy &&
                r.RequestedByAgent == OpsFindingsService.RequestedByAgent &&
                r.IdempotencyKey == finding.Id),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_ActionableFinding_WithoutOperationGateway_ReturnsGatewayUnavailable()
    {
        // Redis-less composition (#2511): OpsFindingsService is constructed without an
        // IOperationGateway (only wired with the durable control-plane graph). Findings still
        // evaluate; proposing an otherwise-actionable fix degrades cleanly instead of throwing.
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.ListActiveAsync(Arg.Any<WorkflowOperationKind?>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkflowOperationRecord>
            {
                BuildDeployOperation("op-9", WorkflowOperationStatus.ManualInterventionRequired, currentRevision: "rev-1", desiredRevision: "rev-2"),
            });

        var service = new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(new ControlPlaneOptions()),
            new FakeAlertDispatchHealth(),
            new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            gateway: null,
            workflowStore: workflowStore);

        var finding = (await service.EvaluateAsync()).Single(f => f.Rule == OpsFindingsService.RuleDeployManualIntervention);
        Assert.NotNull(finding.RecommendedAction);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.GatewayUnavailable, result.Status);
        Assert.Equal(finding.Id, result.FindingId);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ServiceComposition_WithoutRedisOperationGateway_ResolvesOpsFindingsService()
    {
        // Reproduces the #2511 startup failure at the composition level: the server registers
        // IOpsFindingsService unconditionally, but IOperationGateway is only registered when the
        // durable backend (Redis) is present. With ValidateOnBuild — exactly as Program.cs builds
        // the host — resolving the service must NOT throw when the gateway is absent.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<OpsFindingsOptions>(_ => { });
        services.Configure<ControlPlaneOptions>(_ => { });
        services.AddSingleton<IAlertDispatchHealth>(new FakeAlertDispatchHealth());
        services.AddSingleton<IDeployPreflightProbe>(new FakeDeployPreflightProbe(BuildDeploySnapshot()));

        // Deliberately no IOperationGateway / IWorkflowOperationStore / IExecutionJobStore —
        // these are the Redis-gated control-plane registrations.
        services.AddScoped<IOpsFindingsService, OpsFindingsService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IOpsFindingsService>();

        Assert.NotNull(resolved);
    }

    private static OpsFindingsService CreateService(
        OpsFindingsOptions? options = null,
        ControlPlaneOptions? controlPlaneOptions = null,
        FakeAlertDispatchHealth? alertHealth = null,
        FakeDeployPreflightProbe? deployProbe = null,
        IOperationGateway? gateway = null,
        IWorkflowOperationStore? workflowStore = null,
        IExecutionJobStore? jobStore = null)
        => new(
            new StaticOptionsMonitor<OpsFindingsOptions>(options ?? new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(controlPlaneOptions ?? new ControlPlaneOptions()),
            alertHealth ?? new FakeAlertDispatchHealth(),
            deployProbe ?? new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            gateway ?? Substitute.For<IOperationGateway>(),
            workflowStore,
            jobStore);

    private static DeployPreflightSnapshot BuildDeploySnapshot(
        bool hasPendingContractScripts = false,
        IReadOnlyList<string>? pendingContractScripts = null)
        => new()
        {
            Status = "ready",
            ReadyForCoordinatedDeploy = true,
            Message = "ready",
            Readiness = new DeployPreflightReadinessSnapshot { IsReady = true, StatusCode = 200, Message = "ok" },
            Migration = new DeployPreflightMigrationSnapshot
            {
                LifecycleStatus = "succeeded",
                PlanAvailable = true,
                HasPendingContractScripts = hasPendingContractScripts,
                PendingContractScripts = pendingContractScripts ?? Array.Empty<string>(),
            },
        };

    private static ExecutionJobRecord BuildJob(string id, ExecutionJobStatus status, string backend)
        => new()
        {
            OperationId = id,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = backend,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "test-workload",
            },
        };

    private static WorkflowOperationRecord BuildDeployOperation(
        string operationId,
        WorkflowOperationStatus status,
        string? currentRevision,
        string desiredRevision)
        => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Deploy = new DeployOperationSpec
            {
                TargetId = $"target-{operationId}",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "k8s",
                Environment = "prod",
                TargetName = "serving",
                CurrentRevision = currentRevision,
                DesiredRevision = desiredRevision,
            },
        };

    private sealed class FakeAlertDispatchHealth : IAlertDispatchHealth
    {
        public bool IsDispatcherRunning { get; set; }

        public bool IsDispatcherEnabled { get; set; }

        public DateTimeOffset? LastPollAt { get; set; }

        public AlertDispatchBacklog? LastBacklog { get; set; }

        public bool IsStoragePollFailing { get; set; }
    }

    private sealed class FakeDeployPreflightProbe : IDeployPreflightProbe
    {
        private readonly DeployPreflightSnapshot _snapshot;

        public FakeDeployPreflightProbe(DeployPreflightSnapshot snapshot) => _snapshot = snapshot;

        public Task<DeployPreflightSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_snapshot);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
