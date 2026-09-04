// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Executable rollback contract for every automatic backend registered by the host.
/// The provider seams below are deliberately the only fakes: every assertion invokes the
/// registered concrete backend's real RollbackAsync and ObserveAsync implementation.
/// </summary>
public sealed class DeployBackendRollbackTruthfulnessTests
{
    private const string Prior = "revision-prior";
    private const string Candidate = "revision-candidate";
    private const string Other = "revision-other";

    private static readonly string[] RequiredScenarios =
    [
        "exact-prior", "candidate-healthy", "different-revision", "missing-prior",
        "ambiguous-evidence", "transient-recovery", "hard-no-op"
    ];

    private static readonly string[] AutomaticBackends =
    [
        "honua-kubernetes-argo-rollouts", "honua-aws-ecs-alb",
        "honua-azure-container-apps-revision", "honua-gitops-aws-lambda",
        "honua-gitops-azure-functions", "honua-yarp-rolling"
    ];

    [Fact]
    public async Task RegisteredBackends_ExecuteExactPriorRevisionRollbackContract()
    {
        var world = new ProviderWorld();
        using var provider = BuildProvider(world);
        var registered = provider.GetServices<IDeployBackend>().ToArray();
        var capabilities = new Dictionary<string, DeployBackendCapabilities>(StringComparer.Ordinal);
        foreach (var backend in registered)
        {
            capabilities[backend.BackendName] = await backend.GetCapabilitiesAsync();
        }

        var automatic = capabilities.Where(pair => pair.Value.SupportsRollback)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(AutomaticBackends.Order(StringComparer.Ordinal), automatic);

        var rows = new List<RollbackMatrixRow>();
        foreach (var backend in registered.Where(backend => capabilities[backend.BackendName].SupportsRollback))
        {
            foreach (var scenario in RequiredScenarios)
            {
                world.Configure(backend.BackendName, scenario);
                var operation = CreateOperation(backend.BackendName, scenario);
                DeployObservation? rollbackRequest = null;
                var transitions = new List<WorkflowOperationStatus>();
                try
                {
                    rollbackRequest = await backend.RollbackAsync(operation);
                    transitions.Add(rollbackRequest.Status);
                }
                catch (Exception) when (scenario == "transient-recovery")
                {
                    // Provider exceptions are a real transient transition in this executable seam;
                    // the retry below invokes the same registered backend after provider recovery.
                    transitions.Add(WorkflowOperationStatus.Reconciling);
                }
                var observations = new List<DeployObservation>();
                var current = operation with { Status = WorkflowOperationStatus.RollbackRequested };

                if (scenario == "transient-recovery")
                {
                    world.Configure(backend.BackendName, "exact-prior");
                    rollbackRequest = await backend.RollbackAsync(current);
                    transitions.Add(rollbackRequest.Status);
                }

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var observation = await backend.ObserveAsync(current);
                    observations.Add(observation);
                    transitions.Add(observation.Status);
                    if (observation.Status is WorkflowOperationStatus.RolledBack or
                        WorkflowOperationStatus.Failed or WorkflowOperationStatus.ManualInterventionRequired)
                    {
                        break;
                    }
                }

                var terminal = observations.LastOrDefault() ?? rollbackRequest ?? new DeployObservation
                {
                    Status = WorkflowOperationStatus.Failed,
                    Message = "The backend did not return an observation."
                };
                rows.Add(new RollbackMatrixRow(
                    backend.BackendName,
                    capabilities[backend.BackendName].SupportsRollback,
                    scenario,
                    operation.Deploy?.CurrentRevision,
                    terminal.ObservedRevision,
                    world.Describe(backend.BackendName),
                    terminal.Status,
                    transitions,
                    terminal.Message));

                if (scenario is "exact-prior" or "transient-recovery")
                {
                    Assert.Equal(WorkflowOperationStatus.RolledBack, terminal.Status);
                    Assert.Equal(Prior, terminal.ObservedRevision);
                }
                else
                {
                    Assert.DoesNotContain(WorkflowOperationStatus.RolledBack, transitions);
                }
            }
        }

        Assert.Equal(AutomaticBackends.Length * RequiredScenarios.Length, rows.Count);
        WriteEvidence(rows);
    }

    [Fact]
    public async Task RegisteredGitOpsHandoffBackends_AdvertiseRollbackFalse()
    {
        var world = new ProviderWorld();
        using var provider = BuildProvider(world);
        foreach (var backend in provider.GetServices<IDeployBackend>()
                     .Where(backend => backend.BackendName.Contains("gitops", StringComparison.Ordinal) &&
                                       !AutomaticBackends.Contains(backend.BackendName, StringComparer.Ordinal)))
        {
            Assert.False((await backend.GetCapabilitiesAsync()).SupportsRollback, backend.BackendName);
        }
    }

    private static ServiceProvider BuildProvider(ProviderWorld world)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddHttpClient();
        services.AddHonuaBatchAndDeployBackends();
        services.RemoveAll<IArgoRolloutsClient>();
        services.RemoveAll<IAwsAlbClient>();
        services.RemoveAll<IAwsEcsClient>();
        services.RemoveAll<IAwsLambdaAliasClient>();
        services.RemoveAll<IAzureContainerAppsRevisionClient>();
        services.RemoveAll<IAzureFunctionsSlotClient>();
        services.RemoveAll<IContainerRuntimeClient>();
        services.RemoveAll<IProxyStateSwapper>();
        services.RemoveAll<ILocalReplicaHealthProbe>();
        services.AddSingleton<IArgoRolloutsClient>(world);
        services.AddSingleton<IAwsAlbClient>(world);
        services.AddSingleton<IAwsEcsClient>(world);
        services.AddSingleton<IAwsLambdaAliasClient>(world);
        services.AddSingleton<IAzureContainerAppsRevisionClient>(world);
        services.AddSingleton<IAzureFunctionsSlotClient>(world);
        services.AddSingleton<IContainerRuntimeClient>(world);
        services.AddSingleton<IProxyStateSwapper>(world);
        services.AddSingleton<ILocalReplicaHealthProbe>(world);
        services.AddSingleton<IOptions<SelfHostedDeployOptions>>(Options.Create(new SelfHostedDeployOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            ActivePort = 18080,
            StandbyPort = 18081,
            ContainerPort = 8080,
            ContainerRuntime = "docker",
            ContainerNamePrefix = "rollback-contract",
            DrainDelaySeconds = 0
        }));
        // The registration helper also wires unrelated batch and telemetry services whose runtime
        // dependencies are intentionally outside this provider-only contract test. Resolving the
        // registered deploy backend instances below still validates every backend-specific seam.
        return services.BuildServiceProvider();
    }

    private static WorkflowOperationRecord CreateOperation(string backend, string scenario)
    {
        var kind = backend switch
        {
            "honua-aws-ecs-alb" => DeployTargetKind.AwsEcs,
            "honua-azure-container-apps-revision" => DeployTargetKind.AzureContainerApps,
            "honua-gitops-aws-lambda" => DeployTargetKind.AwsLambda,
            "honua-gitops-azure-functions" => DeployTargetKind.AzureFunctions,
            "honua-yarp-rolling" => DeployTargetKind.SelfHostedRolling,
            _ => DeployTargetKind.Kubernetes
        };
        var currentRevision = scenario == "missing-prior" ? null : Prior;
        var parameters = kind switch
        {
            DeployTargetKind.Kubernetes => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesArgoRolloutsDeployBackend.NamespaceParameter] = "honua-prod",
                [KubernetesArgoRolloutsDeployBackend.RolloutNameParameter] = "honua-server",
                [KubernetesArgoRolloutsDeployBackend.ContainerNameParameter] = "honua"
            },
            DeployTargetKind.AwsEcs => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws.region"] = "us-east-1",
                ["aws.ecs.cluster"] = "honua-prod",
                ["aws.ecs.canary_service"] = "honua-canary",
                ["aws.alb.listener_rule_arn"] = "listener-rule",
                ["aws.alb.canary_target_group_arn"] = "canary-tg",
                ["aws.alb.stable_target_group_arn"] = "stable-tg"
            },
            DeployTargetKind.AzureContainerApps => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target.resource_id"] = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/honua"
            },
            DeployTargetKind.AwsLambda => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws.lambda.function_name"] = "honua",
                ["aws.lambda.alias_name"] = "live",
                ["aws.region"] = "us-east-1"
            },
            DeployTargetKind.AzureFunctions => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target.resource_id"] = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/honua",
                ["functions.current_image"] = Prior,
                ["functions.desired_image"] = Candidate,
                ["functions.app_name"] = "honua"
            },
            _ => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfHostedDeployParameterKeys.Image] = Candidate,
                [SelfHostedDeployParameterKeys.ActivePort] = "18080",
                [SelfHostedDeployParameterKeys.StandbyPort] = "18081",
                [SelfHostedDeployParameterKeys.ContainerPort] = "8080"
            }
        };
        return new WorkflowOperationRecord
        {
            OperationId = $"rollback-contract-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.RollbackRequested,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "rollback contract",
            Audit = new OperationAuditInfo(),
            Deploy = new DeployOperationSpec
            {
                TargetId = $"target-{kind}",
                TargetKind = kind,
                Backend = backend,
                Environment = "production",
                TargetName = "honua",
                ArtifactReference = Candidate,
                CurrentRevision = currentRevision,
                DesiredRevision = Candidate,
                Parameters = parameters
            }
        };
    }

    private static void WriteEvidence(IReadOnlyList<RollbackMatrixRow> rows)
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var directory = Environment.GetEnvironmentVariable("HONUA_SERVER_TEST_RESULTS_DIR")
            ?? ArchitectureTestHelpers.CombinePath(root, "tests", "TestResults");
        Directory.CreateDirectory(directory);
        File.WriteAllText(ArchitectureTestHelpers.CombinePath(directory, "deploy-backend-rollback-matrix.json"), JsonSerializer.Serialize(new
        {
            contract = "honua-server#3891",
            executedAtUtc = DateTimeOffset.UtcNow,
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record RollbackMatrixRow(
        string BackendName, bool AdvertisedCapability, string Scenario, string? RequestedPriorRevision,
        string? ProviderReportedRevision, string ProviderReportedState, WorkflowOperationStatus TerminalStatus,
        IReadOnlyList<WorkflowOperationStatus> TransitionSequence, string? ReasonCode);

    private sealed class ProviderWorld : IArgoRolloutsClient, IAwsAlbClient, IAwsEcsClient, IAwsLambdaAliasClient,
        IAzureContainerAppsRevisionClient, IAzureFunctionsSlotClient, IContainerRuntimeClient,
        IProxyStateSwapper, ILocalReplicaHealthProbe
    {
        private string _scenario = "exact-prior";
        private int _rollbackCalls;
        private ArgoRolloutState _argo = Argo(Candidate, false, "candidate-hash");
        private AwsAlbListenerRuleState _alb = Alb(0, 100);
        private AwsEcsServiceState _ecs = Ecs(Candidate);
        private AwsLambdaAliasState _lambda = Lambda(Candidate);
        private AzureContainerAppsTrafficState _containerApp = ContainerApp(Candidate);
        private AzureFunctionsSiteConfigState _production = Functions(Candidate);
        private AzureFunctionsSiteConfigState _slot = Functions(Prior);
        private List<ContainerSummary> _containers = [];
        private string? _activeDestination = "http://127.0.0.1:18081/";

        public bool IsConfigured => true;
        public string? ActiveDestinationAddress => _activeDestination;

        public string Describe(string backend)
            => backend switch
            {
                "honua-kubernetes-argo-rollouts" => $"phase={_argo.Phase};aborted={_argo.IsAborted};currentHash={_argo.CurrentPodHash ?? "<missing>"};stableHash={_argo.StableRevisionHash ?? "<missing>"};image={_argo.PodTemplateImage ?? "<missing>"}",
                "honua-aws-ecs-alb" => $"taskDefinition={_ecs.TaskDefinitionArn ?? "<missing>"};status={_ecs.Status ?? "<missing>"};running={_ecs.RunningCount};desired={_ecs.DesiredCount}",
                "honua-azure-container-apps-revision" => string.Join(',', _containerApp.Traffic.Select(weight => $"{weight.RevisionName}:{weight.Weight}")),
                "honua-gitops-aws-lambda" => $"alias={_lambda.AliasName ?? "<missing>"};version={_lambda.FunctionVersion ?? "<missing>"}",
                "honua-gitops-azure-functions" => $"productionImage={_production.LinuxFxVersion ?? "<missing>"};slotImage={_slot.LinuxFxVersion ?? "<missing>"}",
                "honua-yarp-rolling" => string.Join(',', _containers.Select(container => $"{container.Labels.GetValueOrDefault(YarpRollingDeployBackend.LabelRole, "<missing>")}:{container.Labels.GetValueOrDefault(YarpRollingDeployBackend.LabelRevision, "<missing>")}:{container.Running}")),
                _ => "<unknown>"
            };

        public void Configure(string backend, string scenario)
        {
            _scenario = scenario; _rollbackCalls = 0;
            _argo = Argo(Candidate, false, "candidate-hash"); _alb = Alb(0, 100); _ecs = Ecs(Candidate);
            _lambda = Lambda(Candidate); _containerApp = ContainerApp(Candidate); _production = Functions(Candidate);
            _slot = Functions(Prior); _activeDestination = "http://127.0.0.1:18081/";
            _containers = [Container("rollback-contract-18081", YarpRollingDeployBackend.RoleStandby, Candidate, true)];
            if (backend == "honua-kubernetes-argo-rollouts" && scenario is ("candidate-healthy" or "ambiguous-evidence"))
            {
                _argo = ReadArgoFixture(scenario == "ambiguous-evidence" ? 1 : 0);
            }
            if (scenario == "exact-prior") SetPrior(backend);
            else if (scenario == "different-revision") SetRevision(backend, Other);
            else if (scenario == "ambiguous-evidence")
            {
                _argo = Argo(Candidate, true, null); _ecs = Ecs(null); _containerApp = new(); _lambda = Lambda(null);
            }
        }

        private void SetPrior(string backend)
        {
            SetRevision(backend, Prior);
            if (backend == "honua-kubernetes-argo-rollouts") _argo = Argo(Prior, true, "prior-hash");
        }

        private void SetRevision(string backend, string revision)
        {
            switch (backend)
            {
                case "honua-kubernetes-argo-rollouts": _argo = Argo(revision, true, "prior-hash"); break;
                case "honua-aws-ecs-alb": _ecs = Ecs(revision); break;
                case "honua-azure-container-apps-revision": _containerApp = ContainerApp(revision); break;
                case "honua-gitops-aws-lambda": _lambda = Lambda(revision); break;
                case "honua-gitops-azure-functions": _production = Functions(revision); break;
                case "honua-yarp-rolling":
                    _containers = [Container("rollback-contract-18080", YarpRollingDeployBackend.RoleActive, revision, true)];
                    _activeDestination = "http://127.0.0.1:18080/"; break;
            }
        }

        private bool ShouldFailTransiently() => _scenario == "transient-recovery" && _rollbackCalls++ == 0;
        private bool IsNoOp() => _scenario is "candidate-healthy" or "hard-no-op" or "different-revision" or "ambiguous-evidence";

        public Task<ArgoRolloutState?> GetRolloutAsync(string @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult<ArgoRolloutState?>(_argo);
        public Task<ArgoRolloutState> SetImageAsync(string @namespace, string name, string containerName, string image, CancellationToken cancellationToken = default)
        {
            if (!IsNoOp()) _argo = _argo with { PodTemplateImage = image };
            return Task.FromResult(_argo);
        }
        public Task<ArgoRolloutState> PromoteAsync(string @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult(_argo);
        public Task<ArgoRolloutState> AbortAsync(string @namespace, string name, CancellationToken cancellationToken = default)
        {
            if (ShouldFailTransiently()) throw new HttpRequestException("transient provider failure");
            if (!IsNoOp()) _argo = Argo(Prior, true, "prior-hash");
            return Task.FromResult(_argo);
        }

        public Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(string ruleArn, string? region, CancellationToken cancellationToken = default) => Task.FromResult(_alb);
        public Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(string ruleArn, IReadOnlyList<AwsAlbTargetGroupWeight> weights, string? region, CancellationToken cancellationToken = default)
        {
            if (ShouldFailTransiently()) throw new AmazonClientException("transient provider failure");
            if (!IsNoOp()) _alb = Alb(0, 100); return Task.FromResult(_alb);
        }
        public Task<AwsEcsServiceState> DescribeServiceAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default) => Task.FromResult(_ecs);
        public Task UpdateServiceTaskDefinitionAsync(string cluster, string serviceName, string taskDefinitionArn, string? region, CancellationToken cancellationToken = default)
        {
            if (ShouldFailTransiently()) throw new AmazonClientException("transient provider failure");
            if (!IsNoOp()) _ecs = Ecs(taskDefinitionArn);
            return Task.CompletedTask;
        }

        public Task<AwsLambdaAliasState> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default) => Task.FromResult(_lambda);
        public Task<AwsLambdaAliasState> UpdateAliasAsync(string functionName, string aliasName, string functionVersion, IReadOnlyDictionary<string, double>? additionalVersionWeights, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default)
        {
            if (ShouldFailTransiently()) throw new AmazonClientException("transient provider failure");
            if (!IsNoOp()) _lambda = Lambda(Prior); return Task.FromResult(_lambda);
        }

        public Task<AzureContainerAppsTrafficState> GetTrafficStateAsync(string subscriptionId, string resourceGroupName, string appName, CancellationToken cancellationToken = default) => Task.FromResult(_containerApp);
        public Task<AzureContainerAppsRevisionState> GetRevisionAsync(string subscriptionId, string resourceGroupName, string appName, string revisionName, CancellationToken cancellationToken = default) => Task.FromResult(new AzureContainerAppsRevisionState { RevisionName = revisionName, Active = true, HealthState = "Healthy" });
        public Task<AzureContainerAppsTrafficUpdateResult> UpdateTrafficAsync(string subscriptionId, string resourceGroupName, string appName, IReadOnlyList<AzureContainerAppsTrafficWeight> weights, CancellationToken cancellationToken = default)
        {
            if (ShouldFailTransiently()) throw new HttpRequestException("transient provider failure");
            if (!IsNoOp()) _containerApp = ContainerApp(Prior);
            return Task.FromResult(new AzureContainerAppsTrafficUpdateResult { StatusCode = HttpStatusCode.OK });
        }
        public Task ActivateRevisionAsync(string subscriptionId, string resourceGroupName, string appName, string revisionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AzureFunctionsSiteConfigState> GetSiteConfigAsync(string subscriptionId, string resourceGroupName, string functionAppName, string? slotName, CancellationToken cancellationToken = default) => Task.FromResult(slotName is null ? _production : _slot);
        public Task<AzureFunctionsSlotSwapResult> SwapSlotWithProductionAsync(string subscriptionId, string resourceGroupName, string functionAppName, string slotName, bool preserveVnet, CancellationToken cancellationToken = default)
        {
            if (ShouldFailTransiently()) throw new HttpRequestException("transient provider failure");
            if (!IsNoOp()) _production = Functions(Prior);
            return Task.FromResult(new AzureFunctionsSlotSwapResult { StatusCode = HttpStatusCode.OK });
        }

        public Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<string> RunAsync(ContainerRunRequest request, CancellationToken cancellationToken)
        {
            if (!IsNoOp())
            {
                _containers.RemoveAll(container => container.Name == request.ContainerName);
                _containers.Add(Container(request.ContainerName, YarpRollingDeployBackend.RoleActive, Prior, true));
            }
            return Task.FromResult(request.ContainerName);
        }
        public Task StopAsync(string executable, string containerNameOrId, CancellationToken cancellationToken)
        {
            _containers.RemoveAll(container => container.Name == containerNameOrId); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ContainerSummary>> ListAsync(string executable, IReadOnlyDictionary<string, string> labelSelectors, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContainerSummary>>(_containers.ToArray());
        public Task SwapAsync(string destinationAddress, CancellationToken cancellationToken) { _activeDestination = destinationAddress; return Task.CompletedTask; }
        public Task<LocalReplicaHealthResult> ProbeAsync(string url, int samples, int timeoutSeconds, int expectedStatusCode, CancellationToken cancellationToken) => Task.FromResult(new LocalReplicaHealthResult { Attempts = samples });

        private static ArgoRolloutState Argo(string image, bool aborted, string? hash) => new()
        {
            Name = "honua-server",
            Phase = ArgoRolloutPhase.Healthy,
            IsAborted = aborted,
            PodTemplateImage = image,
            CurrentPodHash = hash,
            StableRevisionHash = hash
        };
        private static AwsAlbListenerRuleState Alb(int canary, int stable) => new()
        {
            ListenerRuleArn = "listener-rule",
            TargetGroupWeights =
            [new AwsAlbTargetGroupWeight { TargetGroupArn = "canary-tg", Weight = canary }, new AwsAlbTargetGroupWeight { TargetGroupArn = "stable-tg", Weight = stable }]
        };
        private static AwsEcsServiceState Ecs(string? revision) => new()
        {
            ServiceName = "honua-canary",
            TaskDefinitionArn = revision,
            RunningCount = 1,
            DesiredCount = 1,
            PendingCount = 0,
            Status = "ACTIVE",
            Deployments = [new AwsEcsDeploymentState
            {
                Status = "PRIMARY", TaskDefinitionArn = revision, RolloutState = "COMPLETED", RunningCount = 1, DesiredCount = 1
            }]
        };
        private static AwsLambdaAliasState Lambda(string? revision) => new() { AliasName = "live", FunctionVersion = revision };
        private static AzureContainerAppsTrafficState ContainerApp(string revision) => new() { Traffic = [new AzureContainerAppsTrafficWeight { RevisionName = revision, Weight = 100 }] };
        private static AzureFunctionsSiteConfigState Functions(string image) => new() { LinuxFxVersion = $"DOCKER|{image}" };
        private static ContainerSummary Container(string name, string role, string revision, bool running) => new()
        {
            Id = name,
            Name = name,
            Running = running,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [YarpRollingDeployBackend.LabelTarget] = "target-SelfHostedRolling",
                [YarpRollingDeployBackend.LabelRole] = role,
                [YarpRollingDeployBackend.LabelRevision] = revision
            }
        };

        private static ArgoRolloutState ReadArgoFixture(int caseIndex)
        {
            var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
            var path = ArchitectureTestHelpers.CombinePath(root, "tests", "dotnet", "Honua.Architecture.Tests", "TestData", "argo-rollback-regression.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var observation = document.RootElement.GetProperty("cases")[caseIndex]
                .GetProperty("observations")[0];
            return new ArgoRolloutState
            {
                Name = "honua-server",
                Phase = ArgoRolloutPhase.Healthy,
                IsAborted = observation.GetProperty("isAborted").GetBoolean(),
                CurrentPodHash = observation.GetProperty("currentPodHash").GetString(),
                StableRevisionHash = observation.GetProperty("stableRevisionHash").GetString(),
                PodTemplateImage = observation.GetProperty("podTemplateImage").GetString()
            };
        }
    }
}
