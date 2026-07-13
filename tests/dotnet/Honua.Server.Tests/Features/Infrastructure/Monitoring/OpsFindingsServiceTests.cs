// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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
    public async Task Evaluate_AlertDispatchDeadLetters_ProducesCriticalFindingWithRedriveAction()
    {
        // #2556: the registered alerts.redrive_dead_letters actuator (#2579) means dead-letters now
        // carry a real recommended action instead of being purely informational.
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
        Assert.Equal("alert-dispatch", finding.Subject.Channel);
        Assert.NotNull(finding.RecommendedAction);
        Assert.Equal(OperationClass.AdminConfigChange, finding.RecommendedAction!.Kind);
        Assert.Contains("\"action\":\"alerts.redrive_dead_letters\"", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_AlertDispatchPendingBacklogOnly_ProducesWarningFindingWithNoAction()
    {
        // A pending-only backlog (no dead-letters yet) has no safe redrive target: informational.
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 300, DeadLetteredCount = 0 },
        };
        var service = CreateService(alertHealth: alertHealth);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.RecommendedAction);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_ChannelDeadLetters_ProducesScopedApprovalOnlyPauseFinding()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastBacklog = new AlertDispatchBacklog
            {
                PendingCount = 8,
                DeadLetteredCount = 2,
                Channels =
                [
                    new AlertDispatchChannelBacklog
                    {
                        ChannelType = AlertChannelType.Email,
                        IsPaused = false,
                        PendingCount = 3,
                        RetryingCount = 2,
                        DeadLetteredCount = 2,
                        OldestItemAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    },
                    new AlertDispatchChannelBacklog
                    {
                        ChannelType = AlertChannelType.Webhook,
                        IsPaused = false,
                        PendingCount = 3,
                        RetryingCount = 0,
                        DeadLetteredCount = 0,
                        OldestItemAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    },
                ],
            },
        };
        var service = CreateService(alertHealth: alertHealth);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleAlertDispatchChannelFailure);
        Assert.Equal("email", finding.Subject.Channel);
        Assert.NotNull(finding.RecommendedAction);
        Assert.Equal(OpsActionNames.PauseChannel, finding.RecommendedAction!.ActionDiscriminator);
        Assert.False(finding.RecommendedAction.AutoSafe);
        Assert.Equal(7, finding.RecommendedAction.BlastRadius);
        Assert.Contains("\"channel\":\"email\"", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_AlreadyPausedFailingChannel_ProducesEvidenceWithoutAnotherAction()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            LastBacklog = new AlertDispatchBacklog
            {
                PendingCount = 0,
                DeadLetteredCount = 1,
                Channels =
                [
                    new AlertDispatchChannelBacklog
                    {
                        ChannelType = AlertChannelType.Slack,
                        IsPaused = true,
                        PendingCount = 0,
                        RetryingCount = 0,
                        DeadLetteredCount = 1,
                        OldestItemAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    },
                ],
            },
        };
        var service = CreateService(alertHealth: alertHealth);

        var finding = (await service.EvaluateAsync())
            .Single(f => f.Rule == OpsFindingsService.RuleAlertDispatchChannelFailure);

        Assert.Equal("slack", finding.Subject.Channel);
        Assert.Null(finding.RecommendedAction);
        Assert.Contains("already paused", finding.Explanation, StringComparison.Ordinal);
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
    public async Task Evaluate_DbPoolPressureWithAdmissionGateHeadroom_ProducesCriticalFindingWithTuneAction()
    {
        var pressureSignal = new FakeDatabasePressureSignal
        {
            Snapshot = new DatabasePressureSnapshot(
                HasConnectionPoolUtilization: true,
                ConnectionPoolUtilization: 0.95,
                ConnectionAcquisitionTimeouts: 10,
                ConnectionAcquisitionFailures: 1),
        };
        var admissionGate = new FakeAdmissionGate { CurrentLimit = 40, MinLimit = 5, MaxLimit = 100 };
        var service = CreateService(databasePressureSignal: pressureSignal, admissionGate: admissionGate);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleDbBoundedAdmissionPressure);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.NotNull(finding.RecommendedAction);
        Assert.Equal(OperationClass.AdminConfigChange, finding.RecommendedAction!.Kind);
        Assert.Contains("\"action\":\"db.tune_bounded_admission\"", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
        // The tuned target must be strictly below the current limit.
        Assert.Contains("\"limit\":30", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_DbPoolPressureWithoutAdmissionGate_ProducesInformationalFinding()
    {
        var pressureSignal = new FakeDatabasePressureSignal
        {
            Snapshot = new DatabasePressureSnapshot(
                HasConnectionPoolUtilization: true,
                ConnectionPoolUtilization: 0.95,
                ConnectionAcquisitionTimeouts: 10,
                ConnectionAcquisitionFailures: 1),
        };
        var service = CreateService(databasePressureSignal: pressureSignal);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleDbBoundedAdmissionPressure);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.Null(finding.RecommendedAction);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_DbPoolBelowThresholds_ProducesNoFinding()
    {
        var pressureSignal = new FakeDatabasePressureSignal
        {
            Snapshot = new DatabasePressureSnapshot(
                HasConnectionPoolUtilization: true,
                ConnectionPoolUtilization: 0.10,
                ConnectionAcquisitionTimeouts: 0,
                ConnectionAcquisitionFailures: 0),
        };
        var service = CreateService(databasePressureSignal: pressureSignal);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RuleDbBoundedAdmissionPressure);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_ServingLatencySloBreach_ProducesInformationalWarning()
    {
        var now = DateTimeOffset.UtcNow;
        var rollupStore = Substitute.For<IOpsHealthRollupStore>();
        rollupStore.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<OpsHealthLatencyRow>
            {
                new()
                {
                    ReplicaId = "replica-1",
                    BucketStart = now.AddMinutes(-1),
                    Point = new OpsHealthLatencyPoint
                    {
                        Protocol = "OgcApiFeatures",
                        RequestCount = 100,
                        ErrorCount = 0,
                        P50Ms = 100,
                        P95Ms = 5000,
                        P99Ms = 6000,
                        MaxMs = 7000,
                    },
                },
            });
        var service = CreateService(rollupStore: rollupStore);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleServingLatencySlo);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Equal("OgcApiFeatures", finding.Subject.Protocol);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_ServingErrorRateSloBreach_ProducesInformationalWarning()
    {
        var now = DateTimeOffset.UtcNow;
        var rollupStore = Substitute.For<IOpsHealthRollupStore>();
        rollupStore.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<OpsHealthLatencyRow>
            {
                new()
                {
                    ReplicaId = "replica-1",
                    BucketStart = now.AddMinutes(-1),
                    Point = new OpsHealthLatencyPoint
                    {
                        Protocol = "WMS",
                        RequestCount = 100,
                        ErrorCount = 8,
                        P50Ms = 50,
                        P95Ms = 150,
                        P99Ms = 200,
                        MaxMs = 250,
                    },
                },
            });
        var service = CreateService(rollupStore: rollupStore);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleServingLatencySlo);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Equal("WMS", finding.Subject.Protocol);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_ServingLatencyBelowThresholds_ProducesNoFinding()
    {
        var now = DateTimeOffset.UtcNow;
        var rollupStore = Substitute.For<IOpsHealthRollupStore>();
        rollupStore.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<OpsHealthLatencyRow>
            {
                new()
                {
                    ReplicaId = "replica-1",
                    BucketStart = now.AddMinutes(-1),
                    Point = new OpsHealthLatencyPoint
                    {
                        Protocol = "OgcApiFeatures",
                        RequestCount = 100,
                        ErrorCount = 0,
                        P50Ms = 50,
                        P95Ms = 150,
                        P99Ms = 200,
                        MaxMs = 250,
                    },
                },
            });
        var service = CreateService(rollupStore: rollupStore);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RuleServingLatencySlo);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_ServingLatencyBelowMinSampleCount_SuppressesFinding()
    {
        // #2809: a single 2.1s request on a quiet protocol yields p95 == 2100ms (nearest-rank of n=1),
        // which would otherwise breach the 2s threshold and cry wolf. The min-sample guard suppresses it.
        var now = DateTimeOffset.UtcNow;
        var rollupStore = Substitute.For<IOpsHealthRollupStore>();
        rollupStore.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<OpsHealthLatencyRow>
            {
                new()
                {
                    ReplicaId = "replica-1",
                    BucketStart = now.AddMinutes(-1),
                    Point = new OpsHealthLatencyPoint
                    {
                        Protocol = "OgcApiFeatures",
                        RequestCount = 1,
                        ErrorCount = 0,
                        P50Ms = 2100,
                        P95Ms = 2100,
                        P99Ms = 2100,
                        MaxMs = 2100,
                    },
                },
            });
        var service = CreateService(rollupStore: rollupStore);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RuleServingLatencySlo);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_ServingLatencyBreachAtMinSampleCount_ProducesFinding()
    {
        // #2809: the same latency breach with a statistically meaningful sample count (>= the guard) still
        // fires — the guard suppresses only the meaningless low-traffic case, never a real breach.
        var now = DateTimeOffset.UtcNow;
        var options = new OpsFindingsOptions { ServingLatencyMinRequestCount = 30 };
        var rollupStore = Substitute.For<IOpsHealthRollupStore>();
        rollupStore.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<OpsHealthLatencyRow>
            {
                new()
                {
                    ReplicaId = "replica-1",
                    BucketStart = now.AddMinutes(-1),
                    Point = new OpsHealthLatencyPoint
                    {
                        Protocol = "OgcApiFeatures",
                        RequestCount = 30,
                        ErrorCount = 0,
                        P50Ms = 100,
                        P95Ms = 2100,
                        P99Ms = 2200,
                        MaxMs = 2500,
                    },
                },
            });
        var service = CreateService(rollupStore: rollupStore, options: options);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleServingLatencySlo);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Equal("OgcApiFeatures", finding.Subject.Protocol);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PlatformReleaseRuntimeDivergence_UnknownTargetTreatedAsDivergent()
    {
        var controlPlane = new ControlPlaneOptions
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.0",
                ServingArtifactReference = "registry/honua-serving:2026.07.0",
                Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "registry/honua-worker:2026.07.0" }],
            },
            DeployTargets = [new DeployTargetOptions { TargetId = "serving-a" }],
        };
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.GetMostRecentSucceededDeployByTargetAsync("serving-a", Arg.Any<CancellationToken>())
            .Returns((WorkflowOperationRecord?)null);
        var service = CreateService(controlPlaneOptions: controlPlane, workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence);
        Assert.Equal(OpsFindingSeverity.Warning, finding.Severity);
        Assert.Equal("serving-a", finding.Subject.TargetId);
        Assert.Equal("2026.07.0", finding.Subject.ReleaseVersion);
        Assert.NotNull(finding.RecommendedAction);
        Assert.Equal(OperationClass.Deploy, finding.RecommendedAction!.Kind);
        Assert.Contains("registry/honua-serving:2026.07.0", finding.RecommendedAction.ExecutionPayload, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PlatformReleaseRuntimeDivergence_MismatchedLastAppliedRevisionIsDivergent()
    {
        var controlPlane = new ControlPlaneOptions
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.0",
                ServingArtifactReference = "registry/honua-serving:2026.07.0",
                Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "registry/honua-worker:2026.07.0" }],
            },
            DeployTargets = [new DeployTargetOptions { TargetId = "serving-a" }],
        };
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.GetMostRecentSucceededDeployByTargetAsync("serving-a", Arg.Any<CancellationToken>())
            .Returns(BuildSucceededDeployOperation("op-1", "serving-a", "registry/honua-serving:OLD"));
        var service = CreateService(controlPlaneOptions: controlPlane, workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence);
        Assert.Contains("registry/honua-serving:OLD", finding.EvidenceRefs.First(e => e.StartsWith("terminal-index:", StringComparison.Ordinal)));
        Assert.NotNull(finding.RecommendedAction);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PlatformReleaseRuntimeDivergence_ConvergedTargetProducesNoFinding()
    {
        var controlPlane = new ControlPlaneOptions
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.0",
                ServingArtifactReference = "registry/honua-serving:2026.07.0",
                Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "registry/honua-worker:2026.07.0" }],
            },
            DeployTargets = [new DeployTargetOptions { TargetId = "serving-a" }],
        };
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.GetMostRecentSucceededDeployByTargetAsync("serving-a", Arg.Any<CancellationToken>())
            .Returns(BuildSucceededDeployOperation("op-1", "serving-a", "registry/honua-serving:2026.07.0"));
        var service = CreateService(controlPlaneOptions: controlPlane, workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PlatformReleaseRuntimeDivergence_ConfigPinnedTargetIsSkipped()
    {
        // A target with an explicit ArtifactReference pin is the platform-release-skew rule's concern —
        // config-derived skew cannot be cleared at runtime, so this rule must not also fire for it.
        var controlPlane = new ControlPlaneOptions
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.0",
                ServingArtifactReference = "registry/honua-serving:2026.07.0",
                Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "registry/honua-worker:2026.07.0" }],
            },
            DeployTargets = [new DeployTargetOptions { TargetId = "serving-pinned", ArtifactReference = "registry/honua-serving:PINNED" }],
        };
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        var service = CreateService(controlPlaneOptions: controlPlane, workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence);
        await workflowStore.DidNotReceive().GetMostRecentSucceededDeployByTargetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_PlatformReleaseRuntimeDivergence_NoReleaseDeclared_ProducesNoFinding()
    {
        var controlPlane = new ControlPlaneOptions
        {
            DeployTargets = [new DeployTargetOptions { TargetId = "serving-a" }],
        };
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        var service = CreateService(controlPlaneOptions: controlPlane, workflowStore: workflowStore);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AllRecommendedActions_AdminConfigChangeActionNamesAreRegistered()
    {
        // RC-2556: every recommendedAction's embedded ops-action name (for AdminConfigChange-kind
        // actions) must exist in the actuator registry (#2579) — enumerate both sides and assert subset.
        var registeredActions = new[]
        {
            OpsActionExecutionPayloads.RedriveDeadLetters(),
            OpsActionExecutionPayloads.TuneBoundedAdmission(10),
        };

        foreach (var payload in registeredActions)
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var action = document.RootElement.GetProperty("action").GetString();
            Assert.NotNull(action);
            Assert.True(
                OpsActionCatalog.IsRegistered(action!),
                $"Ops-findings action '{action}' is not a registered actuator in OpsActionCatalog.");
        }
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
            LastBacklog = new AlertDispatchBacklog { PendingCount = 300, DeadLetteredCount = 0 },
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
    public async Task Propose_AlertDeadLetterFinding_RoutesRedriveActionThroughGateway()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 3, DeadLetteredCount = 2 },
        };
        var gateway = CreateProposalGateway(OperationClass.AdminConfigChange, "proposal-alert-redrive");
        var service = CreateService(alertHealth: alertHealth, gateway: gateway);
        var finding = (await service.EvaluateAsync()).Single(f => f.Rule == OpsFindingsService.RuleAlertDispatchBacklog);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, result.Status);
        Assert.Equal("proposal-alert-redrive", result.ProposalId);
        await gateway.Received(1).RouteAsync(
            Arg.Is<OperationGatewayRequest>(r =>
                r.Kind == OperationClass.AdminConfigChange &&
                r.RequestedByAgent == OpsFindingsService.RequestedByAgent &&
                r.IdempotencyKey == finding.Id &&
                r.ExecutionPayload != null &&
                r.ExecutionPayload.Contains("\"action\":\"alerts.redrive_dead_letters\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_ChannelFailureFinding_RoutesApprovalOnlyScopedPauseThroughGateway()
    {
        var alertHealth = new FakeAlertDispatchHealth
        {
            IsDispatcherEnabled = true,
            LastBacklog = new AlertDispatchBacklog
            {
                PendingCount = 1,
                DeadLetteredCount = 1,
                Channels =
                [
                    new AlertDispatchChannelBacklog
                    {
                        ChannelType = AlertChannelType.MicrosoftTeams,
                        IsPaused = false,
                        PendingCount = 1,
                        RetryingCount = 0,
                        DeadLetteredCount = 1,
                        OldestItemAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    },
                ],
            },
        };
        var gateway = CreateProposalGateway(OperationClass.AdminConfigChange, "proposal-channel-pause");
        var service = CreateService(alertHealth: alertHealth, gateway: gateway);
        var finding = (await service.EvaluateAsync())
            .Single(f => f.Rule == OpsFindingsService.RuleAlertDispatchChannelFailure);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, result.Status);
        await gateway.Received(1).RouteAsync(
            Arg.Is<OperationGatewayRequest>(request =>
                request.ActionDiscriminator == OpsActionNames.PauseChannel &&
                request.AutonomyContext != null &&
                !request.AutonomyContext.ActionMarkedAutoSafe &&
                request.ExecutionPayload != null &&
                request.ExecutionPayload.Contains("\"channel\":\"microsoft_teams\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_DbPoolPressureFinding_RoutesTuneBoundedAdmissionActionThroughGateway()
    {
        var pressureSignal = new FakeDatabasePressureSignal
        {
            Snapshot = new DatabasePressureSnapshot(
                HasConnectionPoolUtilization: true,
                ConnectionPoolUtilization: 0.95,
                ConnectionAcquisitionTimeouts: 10,
                ConnectionAcquisitionFailures: 1),
        };
        var admissionGate = new FakeAdmissionGate { CurrentLimit = 40, MinLimit = 5, MaxLimit = 100 };
        var gateway = CreateProposalGateway(OperationClass.AdminConfigChange, "proposal-db-tune");
        var service = CreateService(
            gateway: gateway,
            databasePressureSignal: pressureSignal,
            admissionGate: admissionGate);
        var finding = (await service.EvaluateAsync()).Single(f => f.Rule == OpsFindingsService.RuleDbBoundedAdmissionPressure);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, result.Status);
        Assert.Equal("proposal-db-tune", result.ProposalId);
        await gateway.Received(1).RouteAsync(
            Arg.Is<OperationGatewayRequest>(r =>
                r.Kind == OperationClass.AdminConfigChange &&
                r.RequestedByAgent == OpsFindingsService.RequestedByAgent &&
                r.IdempotencyKey == finding.Id &&
                r.ExecutionPayload != null &&
                r.ExecutionPayload.Contains("\"action\":\"db.tune_bounded_admission\"", StringComparison.Ordinal) &&
                r.ExecutionPayload.Contains("\"limit\":30", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_PlatformReleaseRuntimeDivergenceFinding_RoutesDeployConvergeActionThroughGateway()
    {
        var controlPlane = new ControlPlaneOptions
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.0",
                ServingArtifactReference = "registry/honua-serving:2026.07.0",
                Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "registry/honua-worker:2026.07.0" }],
            },
            DeployTargets = [new DeployTargetOptions { TargetId = "serving-a" }],
        };
        var workflowStore = Substitute.For<IWorkflowOperationStore>();
        workflowStore.GetMostRecentSucceededDeployByTargetAsync("serving-a", Arg.Any<CancellationToken>())
            .Returns(BuildSucceededDeployOperation("op-old", "serving-a", "registry/honua-serving:OLD"));
        var gateway = CreateProposalGateway(OperationClass.Deploy, "proposal-release-converge");
        var service = CreateService(controlPlaneOptions: controlPlane, gateway: gateway, workflowStore: workflowStore);
        var finding = (await service.EvaluateAsync()).Single(f => f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, result.Status);
        Assert.Equal("proposal-release-converge", result.ProposalId);
        await gateway.Received(1).RouteAsync(
            Arg.Is<OperationGatewayRequest>(r =>
                r.Kind == OperationClass.Deploy &&
                r.RequestedByAgent == OpsFindingsService.RequestedByAgent &&
                r.IdempotencyKey == finding.Id &&
                r.ExecutionPayload != null &&
                r.ExecutionPayload.Contains("\"targetId\":\"serving-a\"", StringComparison.Ordinal) &&
                r.ExecutionPayload.Contains("\"desiredRevision\":\"registry/honua-serving:2026.07.0\"", StringComparison.Ordinal) &&
                r.ExecutionPayload.Contains("\"currentRevision\":\"registry/honua-serving:OLD\"", StringComparison.Ordinal)),
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

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task EvaluateAsync_LocalBackendOnServerlessSubstrate_ProducesCriticalFinding()
    {
        var controlPlane = new ControlPlaneOptions
        {
            Substrate = new SubstrateOptions { Profile = BatchComputeSubstrateProfile.Serverless },
        };

        var service = new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(controlPlane),
            new FakeAlertDispatchHealth(),
            new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            batchBackends: [FakeLocalBackend("honua-local-process", BatchComputeTargetKind.LocalProcess)],
            environmentAccessor: _ => null);

        var findings = await service.EvaluateAsync();

        var finding = Assert.Single(findings, f => f.Rule == OpsFindingsService.RuleLocalBackendSubstrate);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.Contains("honua-local-process", finding.Explanation, StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task EvaluateAsync_LocalBackendOnSingleHostSubstrate_ProducesNoFinding()
    {
        var controlPlane = new ControlPlaneOptions
        {
            Substrate = new SubstrateOptions { Profile = BatchComputeSubstrateProfile.SingleHost },
        };

        var service = new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(controlPlane),
            new FakeAlertDispatchHealth(),
            new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            batchBackends: [FakeLocalBackend("honua-local-process", BatchComputeTargetKind.LocalProcess)],
            environmentAccessor: _ => null);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RuleLocalBackendSubstrate);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task EvaluateAsync_MultiNodeWithSharedWorkDir_ProducesNoFinding()
    {
        var controlPlane = new ControlPlaneOptions
        {
            Substrate = new SubstrateOptions { Profile = BatchComputeSubstrateProfile.MultiNode, SharedWorkDir = true },
        };

        var service = new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(controlPlane),
            new FakeAlertDispatchHealth(),
            new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            batchBackends: [FakeLocalBackend("honua-local-process", BatchComputeTargetKind.LocalProcess)],
            environmentAccessor: _ => null);

        var findings = await service.EvaluateAsync();

        Assert.DoesNotContain(findings, f => f.Rule == OpsFindingsService.RuleLocalBackendSubstrate);
    }

    private static IBatchComputeBackend FakeLocalBackend(string backendName, BatchComputeTargetKind targetKind)
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns(backendName);
        backend.TargetKind.Returns(targetKind);
        return backend;
    }

    private static IOperationGateway CreateProposalGateway(OperationClass operationClass, string proposalId)
    {
        var gateway = Substitute.For<IOperationGateway>();
        gateway.RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.ProposalCreated,
                Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, operationClass, HonuaEdition.Pro, "test"),
                ProposalId = proposalId,
            });
        return gateway;
    }

    private static OpsFindingsService CreateService(
        OpsFindingsOptions? options = null,
        ControlPlaneOptions? controlPlaneOptions = null,
        FakeAlertDispatchHealth? alertHealth = null,
        FakeDeployPreflightProbe? deployProbe = null,
        IOperationGateway? gateway = null,
        IWorkflowOperationStore? workflowStore = null,
        IExecutionJobStore? jobStore = null,
        IOpsDatabasePressureSignal? databasePressureSignal = null,
        IRuntimeTunableAdmissionGate? admissionGate = null,
        IOpsHealthRollupStore? rollupStore = null)
        => new(
            new StaticOptionsMonitor<OpsFindingsOptions>(options ?? new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(controlPlaneOptions ?? new ControlPlaneOptions()),
            alertHealth ?? new FakeAlertDispatchHealth(),
            deployProbe ?? new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            gateway: gateway ?? Substitute.For<IOperationGateway>(),
            workflowStore: workflowStore,
            jobStore: jobStore,
            extendedSignals: new OpsFindingsExtendedSignals
            {
                DatabasePressureSignal = databasePressureSignal,
                AdmissionGate = admissionGate,
                RollupStore = rollupStore,
            });

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

    private static WorkflowOperationRecord BuildSucceededDeployOperation(string operationId, string targetId, string desiredRevision)
        => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Deploy = new DeployOperationSpec
            {
                TargetId = targetId,
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "k8s",
                Environment = "prod",
                TargetName = "serving",
                CurrentRevision = null,
                DesiredRevision = desiredRevision,
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

        public Task<AlertDispatchBacklog> RefreshBacklogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LastBacklog ?? new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 });
    }

    private sealed class FakeDeployPreflightProbe : IDeployPreflightProbe
    {
        private readonly DeployPreflightSnapshot _snapshot;

        public FakeDeployPreflightProbe(DeployPreflightSnapshot snapshot) => _snapshot = snapshot;

        public Task<DeployPreflightSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_snapshot);
    }

    private sealed class FakeDatabasePressureSignal : IOpsDatabasePressureSignal
    {
        public DatabasePressureSnapshot Snapshot { get; set; }

        public DatabasePressureSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class FakeAdmissionGate : IRuntimeTunableAdmissionGate
    {
        public int CurrentLimit { get; set; }

        public int MaxLimit { get; set; } = 100;

        public int MinLimit { get; set; } = 5;

        public bool TrySetLimit(int limit, out string? error)
        {
            error = null;
            CurrentLimit = limit;
            return true;
        }
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
