// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Drives <see cref="RollbackDataPlaneCompletion.Evaluate"/> — the production completion rule —
/// and the real backend observe paths that call it. Nominal weight/alias/traffic convergence
/// without a healthy restored endpoint must not report <see cref="WorkflowOperationStatus.RolledBack"/>.
/// </summary>
public sealed class RollbackDataPlaneHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void Evaluate_HealthyRestoredTarget_Completes()
    {
        var decision = RollbackDataPlaneCompletion.Evaluate(ProvenEvidence(), Now);

        decision.Disposition.Should().Be(RollbackDataPlaneDisposition.RolledBack);
        decision.ReasonCode.Should().Be(RollbackDataPlaneCompletion.ReasonRolledBack);
        decision.ObservedRevision.Should().Be("revision-a");
        RollbackDataPlaneCompletion.ToStatus(decision.Disposition).Should().Be(WorkflowOperationStatus.RolledBack);
    }

    [UnitTest]
    public void Evaluate_ConvergedWeightsWithEmptyStableTargets_StaysNonTerminalThenRequiresIntervention()
    {
        var evidence = ProvenEvidence() with
        {
            HealthyEndpointCount = 0,
            RegisteredEndpointCount = 0
        };

        var waiting = RollbackDataPlaneCompletion.Evaluate(evidence, Now);
        waiting.Disposition.Should().Be(RollbackDataPlaneDisposition.AwaitingDataPlane);
        RollbackDataPlaneCompletion.ToStatus(waiting.Disposition).Should().NotBe(WorkflowOperationStatus.RolledBack);

        var expired = RollbackDataPlaneCompletion.Evaluate(
            evidence with { RollbackStartedAt = Now.AddMinutes(-6) },
            Now);
        expired.Disposition.Should().Be(RollbackDataPlaneDisposition.ManualInterventionRequired);
        expired.ReasonCode.Should().Be(RollbackDataPlaneCompletion.ReasonReadinessUnproven);
        expired.Message.Should().Contain("healthy=0");
        expired.Message.Should().Contain("registered=0");
    }

    [UnitTest]
    public void Evaluate_ConvergedWeightsWithUnhealthyStableTargets_DoesNotComplete()
    {
        var evidence = ProvenEvidence() with
        {
            HealthyEndpointCount = 0,
            RegisteredEndpointCount = 2
        };

        RollbackDataPlaneCompletion.Evaluate(evidence, Now).Disposition
            .Should().Be(RollbackDataPlaneDisposition.AwaitingDataPlane);
        RollbackDataPlaneCompletion.Evaluate(evidence with { RollbackStartedAt = Now.AddMinutes(-6) }, Now)
            .Disposition.Should().Be(RollbackDataPlaneDisposition.ManualInterventionRequired);
    }

    [UnitTheory]
    [InlineData(RollbackFunctionalQueryVerdict.ServedOtherMarker)]
    [InlineData(RollbackFunctionalQueryVerdict.ServedErrorEnvelope)]
    public void Evaluate_WrongOrErrorFunctionalQuery_FailsAfterTheWindow(RollbackFunctionalQueryVerdict verdict)
    {
        var evidence = ProvenEvidence() with
        {
            FunctionalQuery = verdict,
            RollbackStartedAt = Now.AddMinutes(-6)
        };

        var decision = RollbackDataPlaneCompletion.Evaluate(evidence, Now);
        decision.Disposition.Should().Be(RollbackDataPlaneDisposition.Failed);
        decision.ReasonCode.Should().Be(RollbackDataPlaneCompletion.ReasonFunctionalQueryFailed);
        RollbackDataPlaneCompletion.ToStatus(decision.Disposition).Should().NotBe(WorkflowOperationStatus.RolledBack);
    }

    [UnitTest]
    public void Evaluate_ServingIdentityMismatch_RequiresInterventionImmediately()
    {
        var decision = RollbackDataPlaneCompletion.Evaluate(
            ProvenEvidence() with
            {
                PriorRevisionIdentityProven = false,
                ServingIdentityMismatched = true,
                ServingRevision = "revision-b"
            },
            Now);

        decision.Disposition.Should().Be(RollbackDataPlaneDisposition.ManualInterventionRequired);
        decision.ReasonCode.Should().Be(RollbackDataPlaneCompletion.ReasonIdentityMismatch);
        decision.ObservedRevision.Should().Be("revision-b");
    }

    [UnitTest]
    public void Classify_ErrorEnvelopeAndWrongMarker_AreNotAMatch()
    {
        HttpRollbackDataPlaneProbe.Classify(new DeployGoldenQueryResult
        {
            Matched = false,
            Detail = "Golden-query correctness gate failed: the endpoint returned HTTP 200 with a JSON error envelope (top-level \"error\") instead of a result."
        }).Should().Be(RollbackFunctionalQueryVerdict.ServedErrorEnvelope);

        HttpRollbackDataPlaneProbe.Classify(new DeployGoldenQueryResult
        {
            Matched = false,
            Detail = "Golden-query correctness gate failed: response body contained the configured wrong-result marker."
        }).Should().Be(RollbackFunctionalQueryVerdict.ServedOtherMarker);

        HttpRollbackDataPlaneProbe.Classify(new DeployGoldenQueryResult
        {
            Matched = true,
            Detail = "Golden-query correctness gate passed."
        }).Should().Be(RollbackFunctionalQueryVerdict.MatchedPriorMarker);
    }

    [UnitTest]
    public async Task Ecs_EmptyOrUnhealthyStableTargets_WithConvergedWeights_CannotRollBack()
    {
        foreach (var health in new[]
                 {
                     new AwsAlbTargetHealthState(),
                     new AwsAlbTargetHealthState
                     {
                         Targets = [new AwsAlbTargetHealth { TargetId = "stable-1", State = "unhealthy" }]
                     }
                 })
        {
            var backend = CreateEcsBackend(health, out var canaryTaskDefinition);
            var waiting = await backend.ObserveAsync(CreateEcsOperation(expired: false));
            waiting.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
            waiting.ObservedRevision.Should().NotBe(canaryTaskDefinition);
            waiting.Message.Should().Contain("healthy=0");

            var expired = await backend.ObserveAsync(CreateEcsOperation(expired: true));
            expired.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
            expired.ObservedRevision.Should().Be(EcsPriorTask);
            expired.ObservedRevision.Should().NotBe(canaryTaskDefinition);
        }
    }

    [UnitTest]
    public async Task Ecs_HealthyStableTarget_CanComplete_WithoutUsingTheCanaryTaskDefinition()
    {
        var backend = CreateEcsBackend(
            new AwsAlbTargetHealthState
            {
                Targets = [new AwsAlbTargetHealth { TargetId = "stable-1", State = "healthy" }]
            },
            out var canaryTaskDefinition);
        var observation = await backend.ObserveAsync(CreateEcsOperation(expired: false));

        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        observation.ObservedRevision.Should().Be(EcsPriorTask);
        observation.ObservedRevision.Should().NotBe(canaryTaskDefinition);
        observation.Message.Should().Contain("healthy=1");
        observation.Message.Should().Contain(EcsPriorTask);
    }

    [UnitTest]
    public async Task ContainerApps_UnhealthyRevisionAtFullTraffic_CannotRollBack()
    {
        var client = new UnhealthyContainerAppsClient();
        var backend = new AzureContainerAppsRevisionDeployBackend(
            client,
            NullLogger<AzureContainerAppsRevisionDeployBackend>.Instance,
            RollbackDataPlaneTestSupport.HealthyProbe());

        var waiting = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.AzureContainerApps,
            "honua-azure-container-apps-revision",
            "myapp--v1",
            expired: false));
        waiting.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);

        var expired = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.AzureContainerApps,
            "honua-azure-container-apps-revision",
            "myapp--v1",
            expired: true));
        expired.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
    }

    [UnitTest]
    public async Task Lambda_AliasConvergedButQueryServesCandidate_CannotRollBack()
    {
        var probe = RollbackDataPlaneTestSupport.HealthyProbe();
        probe.FunctionalQuery = RollbackFunctionalQueryVerdict.ServedOtherMarker;
        var backend = new AwsLambdaGitOpsDeployBackend(
            new FixedLambdaAlias("41"),
            NullLogger<AwsLambdaGitOpsDeployBackend>.Instance,
            probe);

        var waiting = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.AwsLambda,
            "honua-gitops-aws-lambda",
            "41",
            expired: false,
            extra: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["lambda.function_name"] = "honua"
            }));
        waiting.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);

        var expired = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.AwsLambda,
            "honua-gitops-aws-lambda",
            "41",
            expired: true,
            extra: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["lambda.function_name"] = "honua"
            }));
        expired.Status.Should().Be(WorkflowOperationStatus.Failed);
        expired.ObservedRevision.Should().Be("41");
    }

    [UnitTest]
    public async Task Functions_ImageConvergedButReadinessMissing_CannotRollBack()
    {
        var probe = RollbackDataPlaneTestSupport.HealthyProbe();
        probe.ReadinessProven = false;
        var backend = new AzureFunctionsGitOpsDeployBackend(
            new FixedFunctionsSlots("ghcr.io/honua-io/honua-server:old"),
            NullLogger<AzureFunctionsGitOpsDeployBackend>.Instance,
            probe);

        var waiting = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.AzureFunctions,
            "honua-gitops-azure-functions",
            "production",
            expired: false,
            extra: FunctionsParameters()));
        waiting.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);

        var expired = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.AzureFunctions,
            "honua-gitops-azure-functions",
            "production",
            expired: true,
            extra: FunctionsParameters()));
        expired.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
    }

    [UnitTest]
    public async Task Argo_RevertedButUnhealthy_CannotRollBack()
    {
        var backend = new KubernetesArgoRolloutsDeployBackend(
            new FixedArgoRollout(ArgoRolloutPhase.Degraded),
            NullLogger<KubernetesArgoRolloutsDeployBackend>.Instance,
            RollbackDataPlaneTestSupport.HealthyProbe());

        var waiting = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.Kubernetes,
            "honua-kubernetes-argo-rollouts",
            "honua:prior",
            expired: false,
            extra: ArgoParameters()));
        waiting.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);

        var expired = await backend.ObserveAsync(CreateSimpleOperation(
            DeployTargetKind.Kubernetes,
            "honua-kubernetes-argo-rollouts",
            "honua:prior",
            expired: true,
            extra: ArgoParameters()));
        expired.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
    }

    [UnitTest]
    public async Task Yarp_ConvergedProxyWithUnhealthyOrWrongMarker_CannotRollBack()
    {
        var unhealthy = await ObserveYarpAsync(healthy: false, body: RollbackDataPlaneTestSupport.PriorMarker, expired: false);
        unhealthy.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);

        var wrongMarker = await ObserveYarpAsync(healthy: true, body: RollbackDataPlaneTestSupport.CandidateMarker, expired: true);
        wrongMarker.Status.Should().Be(WorkflowOperationStatus.Failed);

        var errorEnvelope = await ObserveYarpAsync(healthy: true, body: "{\"error\":{\"code\":1}}", expired: true);
        errorEnvelope.Status.Should().Be(WorkflowOperationStatus.Failed);

        var empty = await ObserveYarpAsync(healthy: true, body: RollbackDataPlaneTestSupport.PriorMarker, expired: true, activeRunning: false);
        empty.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
    }

    [UnitTest]
    public async Task Yarp_HealthyRestoredReplica_CanComplete()
    {
        var observation = await ObserveYarpAsync(healthy: true, body: RollbackDataPlaneTestSupport.PriorMarker, expired: false);
        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        observation.ObservedRevision.Should().Be("honua:prior");
    }

    private const string EcsPriorTask = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app:41";
    private const string EcsCanaryTask = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app:42";
    private const string EcsStableService = "honua-prod-stable";

    private static AwsEcsAlbDeployBackend CreateEcsBackend(AwsAlbTargetHealthState health, out string canaryTaskDefinition)
    {
        canaryTaskDefinition = EcsCanaryTask;
        const string canaryTg = "canary-tg";
        const string stableTg = "stable-tg";
        var alb = new EcsAlbStub(health, canaryTg, stableTg);
        var ecs = new EcsServiceStub();
        return new AwsEcsAlbDeployBackend(alb, ecs, NullLogger<AwsEcsAlbDeployBackend>.Instance, RollbackDataPlaneTestSupport.HealthyProbe());
    }

    private static WorkflowOperationRecord CreateEcsOperation(bool expired)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws.region"] = "us-east-1",
            ["aws.ecs.cluster"] = "honua-prod",
            ["aws.ecs.canary_service"] = "honua-canary",
            ["aws.ecs.stable_service"] = EcsStableService,
            ["aws.alb.listener_rule_arn"] = "listener-rule",
            ["aws.alb.canary_target_group_arn"] = "canary-tg",
            ["aws.alb.stable_target_group_arn"] = "stable-tg"
        };
        RollbackDataPlaneTestSupport.AddProof(parameters);
        if (expired)
        {
            RollbackDataPlaneTestSupport.StampExpiredWindow(parameters);
        }

        return Operation(DeployTargetKind.AwsEcs, "honua-aws-ecs-alb", EcsPriorTask, parameters);
    }

    private static async Task<DeployObservation> ObserveYarpAsync(bool healthy, string body, bool expired, bool activeRunning = true)
    {
        var runtime = new YarpRuntimeStub(activeRunning);
        var probe = new YarpProbeStub { Healthy = healthy, Body = body };
        var options = Microsoft.Extensions.Options.Options.Create(new SelfHostedDeployOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            ActivePort = 18080,
            StandbyPort = 18081,
            ContainerPort = 8080,
            ContainerRuntime = "docker",
            ContainerNamePrefix = "rollback-proof",
            DrainDelaySeconds = 0
        });
        var backend = new YarpRollingDeployBackend(
            runtime,
            new YarpProxyStub(),
            probe,
            options,
            NullLogger<YarpRollingDeployBackend>.Instance);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SelfHostedDeployParameterKeys.ActivePort] = "18080",
            [SelfHostedDeployParameterKeys.StandbyPort] = "18081",
            [SelfHostedDeployParameterKeys.ContainerPort] = "8080"
        };
        RollbackDataPlaneTestSupport.AddProof(parameters);
        if (expired)
        {
            RollbackDataPlaneTestSupport.StampExpiredWindow(parameters);
        }

        return await backend.ObserveAsync(Operation(
            DeployTargetKind.SelfHostedRolling,
            YarpRollingDeployBackend.AdapterBackendName,
            "honua:prior",
            parameters));
    }

    private static Dictionary<string, string> FunctionsParameters()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target.resource_id"] = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/honua",
            ["functions.app_name"] = "honua",
            ["functions.slot_name"] = "staging",
            ["functions.current_image"] = "ghcr.io/honua-io/honua-server:old",
            ["functions.desired_image"] = "ghcr.io/honua-io/honua-server:new"
        };
        RollbackDataPlaneTestSupport.AddProof(parameters);
        return parameters;
    }

    private static Dictionary<string, string> ArgoParameters()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KubernetesArgoRolloutsDeployBackend.NamespaceParameter] = "honua-prod",
            [KubernetesArgoRolloutsDeployBackend.RolloutNameParameter] = "honua-server",
            [KubernetesArgoRolloutsDeployBackend.ContainerNameParameter] = "honua"
        };
        RollbackDataPlaneTestSupport.AddProof(parameters);
        return parameters;
    }

    private static WorkflowOperationRecord CreateSimpleOperation(
        DeployTargetKind kind,
        string backend,
        string prior,
        bool expired,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(extra, StringComparer.Ordinal);
        if (kind == DeployTargetKind.AzureContainerApps && parameters.Count == 0)
        {
            parameters["target.resource_id"] = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/honua";
        }

        RollbackDataPlaneTestSupport.AddProof(parameters);
        if (expired)
        {
            RollbackDataPlaneTestSupport.StampExpiredWindow(parameters);
        }

        return Operation(kind, backend, prior, parameters);
    }

    private static WorkflowOperationRecord Operation(
        DeployTargetKind kind,
        string backend,
        string prior,
        IReadOnlyDictionary<string, string> parameters)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"rollback-proof-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.RollbackRequested,
            CreatedAt = now,
            UpdatedAt = now,
            Audit = new OperationAuditInfo(),
            Deploy = new DeployOperationSpec
            {
                TargetId = "target",
                TargetKind = kind,
                Backend = backend,
                Environment = "production",
                TargetName = "honua",
                DesiredRevision = "revision-b",
                CurrentRevision = prior,
                Parameters = parameters
            }
        };
    }

    private static RollbackDataPlaneEvidence ProvenEvidence()
        => new()
        {
            RoutingConverged = true,
            PriorRevisionIdentityProven = true,
            ServingRevision = "revision-a",
            HealthyEndpointCount = 1,
            RegisteredEndpointCount = 1,
            FunctionalQuery = RollbackFunctionalQueryVerdict.MatchedPriorMarker,
            Window = TimeSpan.FromMinutes(5)
        };

    private sealed class EcsAlbStub(AwsAlbTargetHealthState health, string canaryTg, string stableTg) : IAwsAlbClient
    {
        public Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(string ruleArn, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ruleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = canaryTg, Weight = 0 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = stableTg, Weight = 100 }
                ]
            });

        public Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(string ruleArn, IReadOnlyList<AwsAlbTargetGroupWeight> weights, string? region, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AwsAlbTargetHealthState> DescribeTargetHealthAsync(string targetGroupArn, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(health);
    }

    private sealed class EcsServiceStub : IAwsEcsClient
    {
        public Task<AwsEcsServiceState> DescribeServiceAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(new AwsEcsServiceState
            {
                ServiceName = serviceName,
                TaskDefinitionArn = serviceName == EcsStableService ? EcsPriorTask : EcsCanaryTask,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            });

        public Task UpdateServiceTaskDefinitionAsync(string cluster, string serviceName, string taskDefinitionArn, string? region, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class UnhealthyContainerAppsClient : IAzureContainerAppsRevisionClient
    {
        public Task<AzureContainerAppsTrafficState> GetTrafficStateAsync(string subscriptionId, string resourceGroupName, string appName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AzureContainerAppsTrafficState
            {
                Traffic = [new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v1", Weight = 100 }]
            });

        public Task<AzureContainerAppsRevisionState> GetRevisionAsync(string subscriptionId, string resourceGroupName, string appName, string revisionName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AzureContainerAppsRevisionState
            {
                RevisionName = revisionName,
                Active = true,
                HealthState = "Unhealthy",
                RunningState = "Degraded",
                ProvisioningState = "Provisioned"
            });

        public Task<AzureContainerAppsTrafficUpdateResult> UpdateTrafficAsync(string subscriptionId, string resourceGroupName, string appName, IReadOnlyList<AzureContainerAppsTrafficWeight> weights, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ActivateRevisionAsync(string subscriptionId, string resourceGroupName, string appName, string revisionName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FixedLambdaAlias(string version) : IAwsLambdaAliasClient
    {
        public Task<AwsLambdaAliasState> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AwsLambdaAliasState { AliasName = aliasName, FunctionVersion = version });

        public Task<AwsLambdaAliasState> UpdateAliasAsync(string functionName, string aliasName, string functionVersion, IReadOnlyDictionary<string, double>? additionalVersionWeights, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedFunctionsSlots(string productionImage) : IAzureFunctionsSlotClient
    {
        public Task<AzureFunctionsSiteConfigState> GetSiteConfigAsync(string subscriptionId, string resourceGroupName, string functionAppName, string? slotName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = slotName is null ? $"DOCKER|{productionImage}" : "DOCKER|ghcr.io/honua-io/honua-server:new"
            });

        public Task<AzureFunctionsSlotSwapResult> SwapSlotWithProductionAsync(string subscriptionId, string resourceGroupName, string functionAppName, string slotName, bool preserveVnet, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedArgoRollout(ArgoRolloutPhase phase) : IArgoRolloutsClient
    {
        public Task<ArgoRolloutState?> GetRolloutAsync(string @namespace, string name, CancellationToken cancellationToken = default)
            => Task.FromResult<ArgoRolloutState?>(new ArgoRolloutState
            {
                Name = name,
                Phase = phase,
                IsAborted = true,
                PodTemplateImage = "honua:prior",
                CurrentPodHash = "prior-hash",
                StableRevisionHash = "prior-hash"
            });

        public Task<ArgoRolloutState> SetImageAsync(string @namespace, string name, string containerName, string image, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ArgoRolloutState> PromoteAsync(string @namespace, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ArgoRolloutState> AbortAsync(string @namespace, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class YarpRuntimeStub(bool activeRunning) : IContainerRuntimeClient
    {
        public Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string> RunAsync(ContainerRunRequest request, CancellationToken cancellationToken) => Task.FromResult(request.ContainerName);

        public Task StopAsync(string executable, string containerNameOrId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ContainerSummary>> ListAsync(string executable, IReadOnlyDictionary<string, string> labelSelectors, CancellationToken cancellationToken)
        {
            if (!activeRunning)
            {
                return Task.FromResult<IReadOnlyList<ContainerSummary>>([]);
            }

            IReadOnlyList<ContainerSummary> containers =
            [
                new ContainerSummary
                {
                    Id = "rollback-proof-18080",
                    Name = "rollback-proof-18080",
                    Running = true,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [YarpRollingDeployBackend.LabelTarget] = "target",
                        [YarpRollingDeployBackend.LabelRole] = YarpRollingDeployBackend.RoleActive,
                        [YarpRollingDeployBackend.LabelRevision] = "honua:prior"
                    }
                }
            ];
            return Task.FromResult(containers);
        }
    }

    private sealed class YarpProxyStub : IProxyStateSwapper
    {
        public bool IsConfigured => true;
        public string? ActiveDestinationAddress => "http://127.0.0.1:18080/";
        public Task SwapAsync(string destinationAddress, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class YarpProbeStub : ILocalReplicaHealthProbe
    {
        public bool Healthy { get; init; }
        public string? Body { get; init; }

        public Task<LocalReplicaHealthResult> ProbeAsync(string url, int samples, int timeoutSeconds, int expectedStatusCode, CancellationToken cancellationToken)
            => Task.FromResult(new LocalReplicaHealthResult
            {
                Attempts = samples,
                Failures = Healthy ? 0 : samples,
                Reached = true,
                Body = Body
            });
    }
}
