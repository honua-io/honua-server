// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using Yarp.ReverseProxy.Configuration;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Exact-candidate deploy-gate certification (honua-server#4617). Rolls a real published Honua server
/// image (the candidate, <c>HONUA_CANDIDATE_IMAGE</c>) over a distinct real previous image
/// (<c>HONUA_PREVIOUS_IMAGE</c>) through the production deploy workflow service, reconciler, telemetry
/// evaluator and self-hosted rolling backend, with real embedded YARP fronting real docker replicas, a
/// real PostGIS per revision, and a real Prometheus scraping only the candidate replica. Each scenario
/// injects a failure the gate must catch and asserts the recovery against the real front door.
/// </summary>
/// <remarks>
/// <para>
/// The control plane under test is this source tree, hosted in-process; the replicas are the named
/// images. Run the lane from the candidate's own source revision to certify that candidate end to end.
/// Every scenario writes a JSON receipt (image identities, operation timeline, fault and recovery times,
/// the gate's evidence read straight from Prometheus, and front-door traffic counts) to
/// <c>HONUA_CANDIDATE_RECEIPT_DIR</c> when it is set, and to the test output otherwise.
/// </para>
/// <para>
/// Requirements: Docker, <c>HONUA_TEST_REDIS_URL</c> for the control plane's durable workflow store, and
/// both image references. Every test <c>[SkippableFact]</c>-skips when any of them is missing.
/// </para>
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CandidateCertification)]
public sealed class CandidateTelemetryGateCertificationTests : IClassFixture<LocalSubstrateDockerFixture>
{
    private const string CandidateImageEnvVar = "HONUA_CANDIDATE_IMAGE";
    private const string PreviousImageEnvVar = "HONUA_PREVIOUS_IMAGE";
    private const string RedisConnectionStringEnvVar = "HONUA_TEST_REDIS_URL";
    private const string ReceiptDirectoryEnvVar = "HONUA_CANDIDATE_RECEIPT_DIR";
    private const string ControlPlaneSourceShaEnvVar = "HONUA_CONTROL_PLANE_SHA";

    private static readonly TimeSpan CutoverBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SettleBudget = TimeSpan.FromMinutes(5);

    private readonly LocalSubstrateDockerFixture _docker;
    private readonly ITestOutputHelper _output;

    public CandidateTelemetryGateCertificationTests(LocalSubstrateDockerFixture docker, ITestOutputHelper output)
    {
        _docker = docker;
        _output = output;
    }

    [SkippableFact]
    public async Task HealthyCandidate_CutsOverOnStagedChecks_BakesOnRealMetrics_SurvivesControllerRestart_AndCommits()
    {
        var lane = await RequireCandidateLaneAsync();
        await using var env = await CandidateGateEnvironment.StartAsync(_docker, lane, "healthy", _output);
        using var traffic = env.StartTraffic();

        var operationId = await env.CreateDeployAsync();
        var cutover = await env.ReconcileUntilAsync(operationId, op => op.Deploy?.Protection != null || IsTerminal(op.Status), CutoverBudget);

        cutover.Deploy!.Protection.Should().NotBeNull(cutover.CurrentPhase);
        var exposedAt = cutover.Deploy.TrafficExposedAt;
        exposedAt.Should().NotBeNull("the cutover is the candidate's first exposure");
        env.Timeline.Should().Contain(entry => entry.ProtectionPhase == null, "the staged candidate was observed before the cutover");
        env.Timeline.Where(entry => entry.ProtectionPhase == null)
            .Should().OnlyContain(entry => entry.TrafficExposedAt == null, "a staged standby serves no traffic, so it is not exposed");
        env.ActiveProxyDestination().Should().Be(env.StandbyDestination);
        await env.AssertStandbyRunsCandidateImageAsync();

        // Controller restart inside the observation window: a fresh control-plane host resumes the durable
        // operation from Redis while the front door keeps serving.
        await env.RestartControlPlaneAsync();
        var committed = await env.ReconcileUntilAsync(operationId, op => IsTerminal(op.Status), SettleBudget);
        var evidence = await env.QueryGateEvidenceAsync(committed);
        var result = await traffic.StopAsync();
        await env.WriteReceiptAsync("healthy-candidate-commit", committed, faultInjectedAt: null, evidence, result);

        committed.Status.Should().Be(WorkflowOperationStatus.Succeeded, committed.CurrentPhase);
        committed.Deploy!.Protection!.Phase.Should().Be(DeployProtectionPhase.Expired);
        committed.Deploy.TrafficExposedAt.Should().Be(exposedAt, "the persisted exposure stamp survives the controller restart unchanged");
        evidence.SampleCount.Should().BeGreaterThanOrEqualTo(CandidateGateEnvironment.SampleFloor, "the bake read real candidate traffic");
        evidence.ErrorRate.Should().Be(0, "the healthy candidate served no 5xx, and the gate reads that as 0 rather than as absent");
        evidence.LatencyP95Ms.Should().NotBeNull().And.BeLessThan(CandidateGateEnvironment.LatencyThresholdMs);
        result.Failures.Should().Be(0, "the cutover and the controller restart never interrupt the front door");
        result.Statuses.Keys.Should().BeEquivalentTo([200]);
    }

    [SkippableFact]
    public async Task CandidateErrorRegressionAfterCutover_RollsBackToPreviousRevision()
    {
        var lane = await RequireCandidateLaneAsync();
        await using var env = await CandidateGateEnvironment.StartAsync(_docker, lane, "error-regression", _output);
        using var traffic = env.StartTraffic();

        var operationId = await env.CreateDeployAsync();
        var cutover = await env.ReconcileUntilAsync(operationId, op => op.Deploy?.Protection != null || IsTerminal(op.Status), CutoverBudget);
        cutover.Deploy!.Protection.Should().NotBeNull(cutover.CurrentPhase);

        // Injected failure: the candidate's database stops accepting connections, so the candidate keeps
        // answering but its readiness requests fail with HTTP 503.
        var faultInjectedAt = DateTimeOffset.UtcNow;
        await env.RefuseCandidateDatabaseConnectionsAsync();
        var breachEvidence = await env.WaitForCandidateErrorRateAboveThresholdAsync(cutover, TimeSpan.FromMinutes(2));

        var recovered = await env.ReconcileUntilAsync(operationId, op => IsTerminal(op.Status), SettleBudget);
        var frontDoor = await env.GetThroughFrontDoorAsync("/healthz/ready");
        var result = await traffic.StopAsync();
        await env.WriteReceiptAsync("candidate-error-regression-rollback", recovered, faultInjectedAt, breachEvidence, result);

        recovered.Status.Should().Be(WorkflowOperationStatus.RolledBack, recovered.CurrentPhase);
        env.Timeline.Should().Contain(
            entry => entry.Phase != null && entry.Phase.Contains("telemetry detected canary degradation", StringComparison.Ordinal),
            "the rollback was driven by the candidate's real telemetry");
        env.Timeline.Should().NotContain(entry => entry.Status == WorkflowOperationStatus.Succeeded);
        breachEvidence.ErrorRate.Should().NotBeNull().And.BeGreaterThan(CandidateGateEnvironment.ErrorRateThreshold);
        env.ActiveProxyDestination().Should().Be(env.ActiveDestination, "traffic is back on the previous revision");
        frontDoor.Should().Be(200, "the previous revision serves the front door after recovery");
        result.Statuses.Should().ContainKey(503, "the front door saw the regression before the gate recovered from it");
        (DateTimeOffset.UtcNow - faultInjectedAt).Should().BeLessThan(TimeSpan.FromMinutes(6));
    }

    [SkippableFact]
    public async Task PrometheusOutageAfterCutover_KeepsTheWindowUncommitted_ThenRollsBack()
    {
        var lane = await RequireCandidateLaneAsync();
        await using var env = await CandidateGateEnvironment.StartAsync(_docker, lane, "telemetry-outage", _output);
        using var traffic = env.StartTraffic();

        var operationId = await env.CreateDeployAsync();
        var cutover = await env.ReconcileUntilAsync(operationId, op => op.Deploy?.Protection != null || IsTerminal(op.Status), CutoverBudget);
        cutover.Deploy!.Protection.Should().NotBeNull(cutover.CurrentPhase);

        // Injected failure: the telemetry backend disappears right after the candidate is exposed.
        var faultInjectedAt = DateTimeOffset.UtcNow;
        await env.StopPrometheusAsync();

        var recovered = await env.ReconcileUntilAsync(operationId, op => IsTerminal(op.Status), SettleBudget);
        var frontDoor = await env.GetThroughFrontDoorAsync("/healthz/ready");
        var result = await traffic.StopAsync();
        await env.WriteReceiptAsync("telemetry-outage-rollback", recovered, faultInjectedAt, GateEvidence.Unavailable, result);

        recovered.Status.Should().Be(WorkflowOperationStatus.RolledBack, recovered.CurrentPhase);
        env.Timeline.Should().Contain(
            entry => entry.ProtectionReasonCode == DeployWorkflowReconciler.TelemetryEvidencePendingReasonCode &&
                entry.ObservationDeadline != null &&
                entry.ObservedAt > entry.ObservationDeadline,
            "the elapsed window held uncommitted while the telemetry gate had no evidence");
        env.Timeline.Should().Contain(
            entry => entry.Phase != null && entry.Phase.Contains("evidence remained unavailable", StringComparison.Ordinal));
        env.Timeline.Should().NotContain(entry => entry.Status == WorkflowOperationStatus.Succeeded, "missing telemetry never commits a rollout");
        env.ActiveProxyDestination().Should().Be(env.ActiveDestination);
        frontDoor.Should().Be(200);
    }

    [SkippableFact]
    public async Task CandidateNeverHealthy_RollsBackAtTheExposureDeadline_WithoutActivation()
    {
        var lane = await RequireCandidateLaneAsync();
        await using var env = await CandidateGateEnvironment.StartAsync(_docker, lane, "never-healthy", _output);
        using var traffic = env.StartTraffic();

        // Injected failure: the candidate is configured against a database it can never reach, so its
        // standby never becomes ready.
        var faultInjectedAt = DateTimeOffset.UtcNow;
        var operationId = await env.CreateDeployAsync(candidatePostgresHost: "203.0.113.10", exposureDeadlineSeconds: 90);

        var recovered = await env.ReconcileUntilAsync(operationId, op => IsTerminal(op.Status), SettleBudget);
        var result = await traffic.StopAsync();
        await env.WriteReceiptAsync("never-healthy-candidate-rollback", recovered, faultInjectedAt, GateEvidence.Unavailable, result);

        recovered.Status.Should().Be(WorkflowOperationStatus.RolledBack, recovered.CurrentPhase);
        env.Timeline.Should().OnlyContain(entry => entry.TrafficExposedAt == null && entry.ProtectionPhase == null, "the candidate was never activated");
        env.Timeline.Should().Contain(
            entry => entry.Phase != null && entry.Phase.Contains("never passed the backend health gate within the 90-second exposure deadline", StringComparison.Ordinal));
        env.ActiveProxyDestination().Should().Be(env.ActiveDestination);
        result.Failures.Should().Be(0, "the previous revision served the front door throughout");
    }

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private async Task<CandidateLane> RequireCandidateLaneAsync()
    {
        var candidate = Environment.GetEnvironmentVariable(CandidateImageEnvVar);
        var previous = Environment.GetEnvironmentVariable(PreviousImageEnvVar);
        var redis = Environment.GetEnvironmentVariable(RedisConnectionStringEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(previous),
            $"Set {CandidateImageEnvVar} and {PreviousImageEnvVar} to run the exact-candidate deploy-gate lane.");
        Skip.If(string.IsNullOrWhiteSpace(redis), $"Set {RedisConnectionStringEnvVar} for the control plane's durable workflow store.");
        Skip.IfNot(_docker.Available, "Docker is not available for the exact-candidate deploy-gate lane.");

        var candidateImage = await Docker.ResolveImageAsync(candidate!);
        var previousImage = await Docker.ResolveImageAsync(previous!);
        candidateImage.Id.Should().NotBe(previousImage.Id, "the lane certifies a change between two distinct immutable server images");

        return new CandidateLane(
            candidateImage,
            previousImage,
            redis!,
            Environment.GetEnvironmentVariable(ReceiptDirectoryEnvVar),
            Environment.GetEnvironmentVariable(ControlPlaneSourceShaEnvVar));
    }

    private sealed record CandidateLane(
        ImageIdentity Candidate,
        ImageIdentity Previous,
        string RedisConnectionString,
        string? ReceiptDirectory,
        string? ControlPlaneSourceSha);

    private sealed record ImageIdentity(string Reference, string Id, IReadOnlyList<string> RepoDigests);

    private sealed record TimelineEntry(
        DateTimeOffset ObservedAt,
        WorkflowOperationStatus Status,
        string? Phase,
        DateTimeOffset? TrafficExposedAt,
        DeployProtectionPhase? ProtectionPhase,
        string? ProtectionReasonCode,
        DateTimeOffset? ObservationDeadline);

    private sealed record GateEvidence(double? SampleCount, double? ErrorRate, double? LatencyP95Ms, DateTimeOffset? ReadAt)
    {
        public static GateEvidence Unavailable { get; } = new(null, null, null, null);
    }

    private sealed record TrafficResult(int Total, int Failures, IReadOnlyDictionary<int, int> Statuses);

    private sealed record Receipt(
        string Schema,
        string Scenario,
        string? ControlPlaneSourceSha,
        ImageIdentity Candidate,
        ImageIdentity Previous,
        string? StandbyContainerImageId,
        string OperationId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? TrafficExposedAt,
        DateTimeOffset? FaultInjectedAt,
        DateTimeOffset CompletedAt,
        double? RecoverySeconds,
        WorkflowOperationStatus FinalStatus,
        string? FinalPhase,
        GateEvidence Evidence,
        TrafficResult FrontDoorTraffic,
        IReadOnlyList<TimelineEntry> Timeline);

    private sealed class CandidateGateEnvironment : IAsyncDisposable
    {
        internal const double SampleFloor = 20;
        internal const double ErrorRateThreshold = 0.05;
        internal const double LatencyThresholdMs = 2000;

        private const string PostgresImage = "postgis/postgis:18-3.6";
        private const string RedisImage = "redis:7.2-alpine";
        private const string PrometheusImage = "prom/prometheus:v3.5.0";
        private const string ControlPlaneAdminPassword = "Candidate-Gate-4617!control";
        private const string ReplicaAdminPassword = "Candidate-Gate-4617!replica";
        private const string DatabasePassword = "candidate-gate-4617";
        private const string ReplicaHostAlias = "host.docker.internal";
        private const string PrometheusJob = "honua-candidate";
        private const string TelemetryConnectionId = "candidate-prometheus";
        private const int ReplicaContainerPort = 8080;

        private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly LocalSubstrateDockerFixture _docker;
        private readonly CandidateLane _lane;
        private readonly ITestOutputHelper _output;
        private readonly string _suffix;
        private readonly string _containerPrefix;
        private readonly int _activePort;
        private readonly int _standbyPort;
        private readonly int _prometheusPort;
        private readonly string _prometheusConfigDirectory;
        private readonly string _candidatePostgresName;
        private readonly string _candidatePostgresAddress;
        private readonly string _redisAddress;
        private readonly Dictionary<string, string?> _hostSettings;
        private readonly EnvironmentVariableScope _environmentScope;
        private readonly WebApplication _proxy;
        private readonly IProxyStateSwapper _swapper;
        private WebApplicationFactory<Program> _factory;

        private CandidateGateEnvironment(
            LocalSubstrateDockerFixture docker,
            CandidateLane lane,
            ITestOutputHelper output,
            string suffix,
            string targetId,
            string containerPrefix,
            (int Active, int Standby, int Prometheus) ports,
            string prometheusConfigDirectory,
            (string Name, string Address) candidatePostgres,
            string redisAddress,
            Dictionary<string, string?> hostSettings,
            EnvironmentVariableScope environmentScope,
            WebApplication proxy,
            IProxyStateSwapper swapper)
        {
            _docker = docker;
            _lane = lane;
            _output = output;
            _suffix = suffix;
            TargetId = targetId;
            _containerPrefix = containerPrefix;
            _activePort = ports.Active;
            _standbyPort = ports.Standby;
            _prometheusPort = ports.Prometheus;
            _prometheusConfigDirectory = prometheusConfigDirectory;
            _candidatePostgresName = candidatePostgres.Name;
            _candidatePostgresAddress = candidatePostgres.Address;
            _redisAddress = redisAddress;
            _hostSettings = hostSettings;
            _environmentScope = environmentScope;
            _proxy = proxy;
            _swapper = swapper;
            _factory = CreateFactory(hostSettings, swapper);
            ProxyBaseUrl = proxy.Urls.First().TrimEnd('/');
        }

        public string TargetId { get; }

        public string ProxyBaseUrl { get; }

        public List<TimelineEntry> Timeline { get; } = [];

        public string ActiveDestination => $"http://127.0.0.1:{_activePort.ToString(CultureInfo.InvariantCulture)}/";

        public string StandbyDestination => $"http://127.0.0.1:{_standbyPort.ToString(CultureInfo.InvariantCulture)}/";

        private string PrometheusBaseUrl => $"http://127.0.0.1:{_prometheusPort.ToString(CultureInfo.InvariantCulture)}";

        private string StandbyContainerName => $"{_containerPrefix}-{_standbyPort.ToString(CultureInfo.InvariantCulture)}";

        public static async Task<CandidateGateEnvironment> StartAsync(
            LocalSubstrateDockerFixture docker,
            CandidateLane lane,
            string scenario,
            ITestOutputHelper output)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var targetId = $"honua-candidate-gate-{scenario}-{suffix}";
            var containerPrefix = $"honua-cg-{suffix}";
            var ports = AllocatePorts();
            var labels = new[] { "--label", $"{YarpRollingDeployBackend.LabelTarget}={targetId}" };
            var configDirectory = Directory.CreateTempSubdirectory("honua-candidate-gate").FullName;
            WebApplication? proxy = null;
            EnvironmentVariableScope? environmentScope = null;

            try
            {
                var previousPostgres = await StartPostgresAsync($"{containerPrefix}-pg-previous", labels);
                var candidatePostgres = await StartPostgresAsync($"{containerPrefix}-pg-candidate", labels);
                var redisName = $"{containerPrefix}-redis";
                await Docker.RunCheckedAsync(["run", "-d", "--name", redisName, .. labels, RedisImage]);
                var redisAddress = await Docker.ContainerAddressAsync(redisName);
                await StartPrometheusAsync($"{containerPrefix}-prometheus", labels, configDirectory, ports);

                // Revision A: the previous image, already serving behind the front door.
                await docker.Runtime.RunAsync(
                    new ContainerRunRequest
                    {
                        Executable = docker.RuntimeExecutable,
                        Image = lane.Previous.Reference,
                        ContainerName = $"{containerPrefix}-{ports.Active.ToString(CultureInfo.InvariantCulture)}",
                        HostPort = ports.Active,
                        ContainerPort = ReplicaContainerPort,
                        Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [YarpRollingDeployBackend.LabelTarget] = targetId,
                            [YarpRollingDeployBackend.LabelRole] = YarpRollingDeployBackend.RoleActive,
                            [YarpRollingDeployBackend.LabelRevision] = lane.Previous.Reference
                        },
                        Environment = ReplicaEnvironment(previousPostgres.Address, redisAddress)
                    },
                    CancellationToken.None);
                (await WaitForStatusAsync(ReplicaUrl(ports.Active, "/healthz/ready"), 200, TimeSpan.FromMinutes(3)))
                    .Should().BeTrue("the previous revision must be serving before the candidate is rolled over it");

                var options = new SelfHostedDeployOptions
                {
                    Enabled = true,
                    ContainerRuntime = docker.RuntimeExecutable,
                    ContainerNamePrefix = containerPrefix,
                    Host = "127.0.0.1",
                    HealthPath = "/healthz/ready",
                    ActivePort = ports.Active,
                    StandbyPort = ports.Standby,
                    ContainerPort = ReplicaContainerPort,
                    HealthProbeSamples = 2,
                    HealthProbeTimeoutSeconds = 5,
                    HealthProbeExpectedStatusCode = 200,
                    DrainDelaySeconds = 1
                };

                var proxyBuilder = WebApplication.CreateBuilder();
                proxyBuilder.Logging.ClearProviders();
                proxyBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
                proxyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
                proxyBuilder.Services.AddReverseProxy().LoadFromMemory(
                    SelfHostedProxyConfig.BuildRoutes(options),
                    SelfHostedProxyConfig.BuildClusters(options, YarpInMemoryProxyStateSwapper.InitialActiveAddress(options)));
                proxy = proxyBuilder.Build();
                proxy.MapReverseProxy();
                await proxy.StartAsync();

                var configProvider = (InMemoryConfigProvider)proxy.Services.GetRequiredService<IProxyConfigProvider>();
                var swapper = new YarpInMemoryProxyStateSwapper(configProvider, docker.Runtime, Options.Create(options));
                var settings = BuildHostSettings(lane.RedisConnectionString, targetId, containerPrefix, docker.RuntimeExecutable, ports);
                environmentScope = EnvironmentVariableScope.Apply(settings);

                return new CandidateGateEnvironment(
                    docker,
                    lane,
                    output,
                    suffix,
                    targetId,
                    containerPrefix,
                    ports,
                    configDirectory,
                    candidatePostgres,
                    redisAddress,
                    settings,
                    environmentScope,
                    proxy,
                    swapper);
            }
            catch
            {
                environmentScope?.Dispose();
                if (proxy != null)
                {
                    await proxy.DisposeAsync();
                }

                await docker.CleanupTargetAsync(targetId);
                TryDeleteDirectory(configDirectory);
                throw;
            }
        }

        public async Task<string> CreateDeployAsync(string? candidatePostgresHost = null, int exposureDeadlineSeconds = 300)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfHostedDeployParameterKeys.Image] = _lane.Candidate.Reference,
                [SelfHostedDeployParameterKeys.ActivePort] = _activePort.ToString(CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.StandbyPort] = _standbyPort.ToString(CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.ContainerPort] = ReplicaContainerPort.ToString(CultureInfo.InvariantCulture),
                ["telemetry.connection"] = TelemetryConnectionId,
                ["telemetry.prometheus.job"] = PrometheusJob,
                ["telemetry.warmup_seconds"] = "20",
                ["telemetry.evidence_grace_seconds"] = "30",
                ["telemetry.sample_count.minimum"] = SampleFloor.ToString(CultureInfo.InvariantCulture),
                ["telemetry.error_rate.threshold"] = ErrorRateThreshold.ToString(CultureInfo.InvariantCulture),
                ["telemetry.latency_p95.threshold_ms"] = LatencyThresholdMs.ToString(CultureInfo.InvariantCulture),
                ["telemetry.max_staleness_seconds"] = "60",
                ["telemetry.exposure_deadline_seconds"] = exposureDeadlineSeconds.ToString(CultureInfo.InvariantCulture),
                [DeployWorkflowReconciler.ProtectionObservationWindowSecondsParameterKey] = "30"
            };
            foreach (var (name, value) in ReplicaEnvironment(candidatePostgresHost ?? _candidatePostgresAddress, _redisAddress))
            {
                parameters[SelfHostedDeployParameterKeys.EnvironmentPrefix + name] = value;
            }

            var service = _factory.Services.GetRequiredService<DeployWorkflowService>();
            var record = await service.CreateAsync(
                TargetId,
                _lane.Candidate.Reference,
                _lane.Previous.Reference,
                requestedBy: "candidate-gate-certification",
                reason: "Roll the exact candidate over the previous revision under an injected failure.",
                idempotencyKey: $"candidate-gate-{_suffix}",
                correlationId: $"candidate-gate-{_suffix}",
                OperationPriority.Normal,
                submitImmediately: true,
                parameters,
                principal: null,
                cancellationToken: CancellationToken.None);

            record.Should().NotBeNull("the self-hosted rolling target must be resolvable");
            record!.Status.Should().NotBe(WorkflowOperationStatus.Failed, record.ErrorMessage);
            Record(record);
            return record.OperationId;
        }

        public async Task<WorkflowOperationRecord> ReconcileUntilAsync(
            string operationId,
            Func<WorkflowOperationRecord, bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            WorkflowOperationRecord? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await _factory.Services.GetRequiredService<IOperationReconcileDispatcher>()
                    .ReconcileOnceAsync(new OperationRef(OperationKind.DeployWorkflow, operationId));
                last = await _factory.Services.GetRequiredService<DeployWorkflowService>().GetAsync(operationId);
                if (last != null)
                {
                    Record(last);
                    if (predicate(last))
                    {
                        return last;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new TimeoutException(
                $"Deploy operation '{operationId}' did not reach the expected state within {timeout}. " +
                $"Last status: {last?.Status.ToString() ?? "(missing)"} ({last?.CurrentPhase}).");
        }

        public async Task RestartControlPlaneAsync()
        {
            _output.WriteLine("Restarting the control-plane host.");
            await _factory.DisposeAsync();
            _factory = CreateFactory(_hostSettings, _swapper);
        }

        public string? ActiveProxyDestination() => _swapper.ActiveDestinationAddress;

        public async Task AssertStandbyRunsCandidateImageAsync()
            => (await StandbyContainerImageIdAsync()).Should().Be(_lane.Candidate.Id, "the exposed replica runs the exact candidate image");

        public TrafficLoop StartTraffic() => TrafficLoop.Start(ProxyBaseUrl);

        public async Task<int> GetThroughFrontDoorAsync(string path)
        {
            using var client = new HttpClient { BaseAddress = new Uri(ProxyBaseUrl), Timeout = TimeSpan.FromSeconds(20) };
            var status = 0;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var response = await client.GetAsync(path);
                status = (int)response.StatusCode;
                if (status == 200)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return status;
        }

        public Task RefuseCandidateDatabaseConnectionsAsync()
        {
            _output.WriteLine("Injecting failure: the candidate database refuses connections.");
            return Docker.RunCheckedAsync(
            [
                "exec", _candidatePostgresName, "psql", "-U", "honua", "-d", "postgres", "-v", "ON_ERROR_STOP=1",
                "-c", "ALTER DATABASE honua ALLOW_CONNECTIONS false",
                "-c", "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'honua'"
            ]);
        }

        public Task StopPrometheusAsync()
        {
            _output.WriteLine("Injecting failure: the telemetry backend is removed.");
            return Docker.RunCheckedAsync(["rm", "-f", $"{_containerPrefix}-prometheus"]);
        }

        public async Task<GateEvidence> QueryGateEvidenceAsync(WorkflowOperationRecord operation)
        {
            var policy = DeployTelemetryPolicy.Parse(operation.Deploy!);
            policy.Should().NotBeNull();
            using var client = new HttpClient { BaseAddress = new Uri(PrometheusBaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            return new GateEvidence(
                await QueryScalarAsync(client, policy!.MinimumSampleQuery!),
                await QueryScalarAsync(client, policy.ErrorRateQuery!),
                await QueryScalarAsync(client, policy.LatencyP95Query!),
                DateTimeOffset.UtcNow);
        }

        public async Task<GateEvidence> WaitForCandidateErrorRateAboveThresholdAsync(WorkflowOperationRecord operation, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            var evidence = GateEvidence.Unavailable;
            while (DateTimeOffset.UtcNow < deadline)
            {
                evidence = await QueryGateEvidenceAsync(operation);
                if (evidence.ErrorRate > ErrorRateThreshold)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return evidence;
        }

        public async Task WriteReceiptAsync(
            string scenario,
            WorkflowOperationRecord operation,
            DateTimeOffset? faultInjectedAt,
            GateEvidence evidence,
            TrafficResult traffic)
        {
            var completedAt = operation.CompletedAt ?? DateTimeOffset.UtcNow;
            var receipt = new Receipt(
                Schema: "honua.candidate-deploy-gate-receipt/v1",
                Scenario: scenario,
                ControlPlaneSourceSha: _lane.ControlPlaneSourceSha,
                Candidate: _lane.Candidate,
                Previous: _lane.Previous,
                StandbyContainerImageId: await StandbyContainerImageIdAsync(),
                OperationId: operation.OperationId,
                CreatedAt: operation.CreatedAt,
                TrafficExposedAt: operation.Deploy?.TrafficExposedAt,
                FaultInjectedAt: faultInjectedAt,
                CompletedAt: completedAt,
                RecoverySeconds: faultInjectedAt is { } fault ? (completedAt - fault).TotalSeconds : null,
                FinalStatus: operation.Status,
                FinalPhase: operation.CurrentPhase,
                Evidence: evidence,
                FrontDoorTraffic: traffic,
                Timeline: Timeline);
            var json = JsonSerializer.Serialize(receipt, ReceiptJsonOptions);
            _output.WriteLine(json);

            if (!string.IsNullOrWhiteSpace(_lane.ReceiptDirectory))
            {
                Directory.CreateDirectory(_lane.ReceiptDirectory);
                await File.WriteAllTextAsync(Path.Join(_lane.ReceiptDirectory, $"{scenario}.json"), json);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            _environmentScope.Dispose();
            try
            {
                await _proxy.StopAsync();
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Best-effort proxy shutdown; the containers are removed below either way.
            }

            await _proxy.DisposeAsync();
            await _docker.CleanupTargetAsync(TargetId);
            TryDeleteDirectory(_prometheusConfigDirectory);
        }

        private void Record(WorkflowOperationRecord operation)
        {
            var protection = operation.Deploy?.Protection;
            var entry = new TimelineEntry(
                DateTimeOffset.UtcNow,
                operation.Status,
                operation.CurrentPhase,
                operation.Deploy?.TrafficExposedAt,
                protection?.Phase,
                protection?.ReasonCode,
                protection?.ObservationDeadline);
            var previous = Timeline.Count > 0 ? Timeline[^1] : null;
            if (previous == null ||
                previous.Status != entry.Status ||
                !string.Equals(previous.Phase, entry.Phase, StringComparison.Ordinal) ||
                previous.TrafficExposedAt != entry.TrafficExposedAt ||
                previous.ProtectionPhase != entry.ProtectionPhase ||
                !string.Equals(previous.ProtectionReasonCode, entry.ProtectionReasonCode, StringComparison.Ordinal))
            {
                _output.WriteLine($"{entry.ObservedAt:O} {entry.Status} exposed={entry.TrafficExposedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-"} protection={entry.ProtectionPhase?.ToString() ?? "-"}/{entry.ProtectionReasonCode ?? "-"} {entry.Phase}");
            }

            // Keep every observation past the window deadline so the pending-evidence hold stays provable.
            Timeline.Add(entry);
        }

        private async Task<string?> StandbyContainerImageIdAsync()
        {
            var (exitCode, stdout, _) = await Docker.RunAsync(["inspect", "-f", "{{.Image}}", StandbyContainerName]);
            return exitCode == 0 ? stdout.Trim() : null;
        }

        private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings, IProxyStateSwapper swapper)
            => new TestWebApplicationFactory()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IProxyStateSwapper>();
                        services.AddSingleton(swapper);
                    });
                });

        private static async Task<(string Name, string Address)> StartPostgresAsync(string name, string[] labels)
        {
            await Docker.RunCheckedAsync(
            [
                "run", "-d", "--name", name, .. labels,
                "-e", "POSTGRES_USER=honua", "-e", $"POSTGRES_PASSWORD={DatabasePassword}", "-e", "POSTGRES_DB=honua",
                PostgresImage
            ]);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                // pg_isready over TCP: the image's init phase answers on the unix socket before the final
                // server that the replicas connect to is listening.
                var (exitCode, _, _) = await Docker.RunAsync(["exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "honua", "-d", "honua"]);
                if (exitCode == 0)
                {
                    return (name, await Docker.ContainerAddressAsync(name));
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new InvalidOperationException($"PostGIS container '{name}' did not become ready.");
        }

        private static async Task StartPrometheusAsync(
            string name,
            string[] labels,
            string configDirectory,
            (int Active, int Standby, int Prometheus) ports)
        {
            // Prometheus scrapes only the standby port, where the candidate replica runs from launch until
            // rollback, so every series it stores describes the candidate. The scrape goes through the
            // published host port because the replicas are started by the backend on the default network.
            var config = $$"""
                global:
                  scrape_interval: 2s
                  evaluation_interval: 2s
                scrape_configs:
                  - job_name: {{PrometheusJob}}
                    metrics_path: /metrics
                    http_headers:
                      X-API-Key:
                        values: ["{{ReplicaAdminPassword}}"]
                    static_configs:
                      - targets: ["{{ReplicaHostAlias}}:{{ports.Standby.ToString(CultureInfo.InvariantCulture)}}"]
                """;
            var configPath = Path.Join(configDirectory, "prometheus.yml");
            await File.WriteAllTextAsync(configPath, config);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    configDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            await Docker.RunCheckedAsync(
            [
                "run", "-d", "--name", name, .. labels,
                "--add-host", $"{ReplicaHostAlias}:host-gateway",
                "-p", $"127.0.0.1:{ports.Prometheus.ToString(CultureInfo.InvariantCulture)}:9090",
                "-v", $"{configDirectory}:/etc/prometheus:ro",
                PrometheusImage,
                "--config.file=/etc/prometheus/prometheus.yml"
            ]);

            var readyUrl = $"http://127.0.0.1:{ports.Prometheus.ToString(CultureInfo.InvariantCulture)}/-/ready";
            (await WaitForStatusAsync(readyUrl, 200, TimeSpan.FromMinutes(1)))
                .Should().BeTrue("the candidate's telemetry backend must be ready before the rollout");
        }

        private static Dictionary<string, string> ReplicaEnvironment(string postgresHost, string redisAddress)
            => new(StringComparer.Ordinal)
            {
                ["ConnectionStrings__DefaultConnection"] = $"Host={postgresHost};Database=honua;Username=honua;Password={DatabasePassword}",
                ["ConnectionStrings__Redis"] = $"{redisAddress}:6379",
                ["HONUA_ADMIN_PASSWORD"] = ReplicaAdminPassword,
                ["HostValidation__AllowedHosts__0"] = "127.0.0.1",
                ["HostValidation__AllowedHosts__1"] = "localhost",
                ["HostValidation__AllowedHosts__2"] = ReplicaHostAlias,
                ["Security__ConnectionEncryption__MasterKey"] = "candidate-gate-4617-master-key-0123456789abcdef",
                ["Security__ConnectionEncryption__Salt"] = "Y2FuZGlkYXRlLWdhdGUtNDYxNy1zYWx0"
            };

        private static Dictionary<string, string?> BuildHostSettings(
            string redisConnectionString,
            string targetId,
            string containerPrefix,
            string containerRuntime,
            (int Active, int Standby, int Prometheus) ports)
        {
            var active = ports.Active.ToString(CultureInfo.InvariantCulture);
            var standby = ports.Standby.ToString(CultureInfo.InvariantCulture);
            var containerPort = ReplicaContainerPort.ToString(CultureInfo.InvariantCulture);

            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HONUA_DEV_AUTH"] = "false",
                ["HONUA_ADMIN_PASSWORD"] = ControlPlaneAdminPassword,
                ["ConnectionStrings:redis"] = redisConnectionString,
                ["Cache:KeyPrefix"] = $"honua:{targetId}:",
                ["Licensing:DevGrantEdition"] = "Pro",
                ["ControlPlane:SelfHosted:Enabled"] = "false",
                ["ControlPlane:SelfHosted:ContainerRuntime"] = containerRuntime,
                ["ControlPlane:SelfHosted:ContainerNamePrefix"] = containerPrefix,
                ["ControlPlane:SelfHosted:Host"] = "127.0.0.1",
                ["ControlPlane:SelfHosted:HealthPath"] = "/healthz/ready",
                ["ControlPlane:SelfHosted:ActivePort"] = active,
                ["ControlPlane:SelfHosted:StandbyPort"] = standby,
                ["ControlPlane:SelfHosted:ContainerPort"] = containerPort,
                ["ControlPlane:SelfHosted:HealthProbeSamples"] = "2",
                ["ControlPlane:SelfHosted:HealthProbeTimeoutSeconds"] = "5",
                ["ControlPlane:SelfHosted:HealthProbeExpectedStatusCode"] = "200",
                ["ControlPlane:SelfHosted:DrainDelaySeconds"] = "1",
                ["ControlPlane:DeployTargets:0:TargetId"] = targetId,
                ["ControlPlane:DeployTargets:0:TargetKind"] = DeployTargetKind.SelfHostedRolling.ToString(),
                ["ControlPlane:DeployTargets:0:Backend"] = YarpRollingDeployBackend.AdapterBackendName,
                ["ControlPlane:DeployTargets:0:Environment"] = "candidate-certification",
                ["ControlPlane:DeployTargets:0:TargetName"] = "honua-serving",
                ["ControlPlane:DeployTargets:0:RequiresApproval"] = "false",
                ["ControlPlane:DeployTargets:0:ParameterEntries:0:Key"] = SelfHostedDeployParameterKeys.ActivePort,
                ["ControlPlane:DeployTargets:0:ParameterEntries:0:Value"] = active,
                ["ControlPlane:DeployTargets:0:ParameterEntries:1:Key"] = SelfHostedDeployParameterKeys.StandbyPort,
                ["ControlPlane:DeployTargets:0:ParameterEntries:1:Value"] = standby,
                ["ControlPlane:DeployTargets:0:ParameterEntries:2:Key"] = SelfHostedDeployParameterKeys.ContainerPort,
                ["ControlPlane:DeployTargets:0:ParameterEntries:2:Value"] = containerPort,
                ["ControlPlane:TelemetryConnections:0:ConnectionId"] = TelemetryConnectionId,
                ["ControlPlane:TelemetryConnections:0:Provider"] = "prometheus",
                ["ControlPlane:TelemetryConnections:0:BaseUrl"] = $"http://127.0.0.1:{ports.Prometheus.ToString(CultureInfo.InvariantCulture)}",
                ["ControlPlane:TelemetryConnections:0:AllowPrivateNetworks"] = "true",
                ["ControlPlane:TelemetryConnections:0:TimeoutSeconds"] = "5"
            };
        }

        private static (int Active, int Standby, int Prometheus) AllocatePorts()
        {
            var ports = new HashSet<int>();
            while (ports.Count < 3)
            {
                ports.Add(LocalSubstrateDockerFixture.GetFreeTcpPort());
            }

            var allocated = ports.ToArray();
            return (allocated[0], allocated[1], allocated[2]);
        }

        private static string ReplicaUrl(int port, string path)
            => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{path}";

        private static async Task<bool> WaitForStatusAsync(string url, int expectedStatus, TimeSpan timeout)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync(url);
                    if ((int)response.StatusCode == expectedStatus)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Still starting.
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            return false;
        }

        private static async Task<double?> QueryScalarAsync(HttpClient client, string query)
        {
            try
            {
                using var response = await client.GetAsync($"/api/v1/query?query={Uri.EscapeDataString(query)}");
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var result = document.RootElement.GetProperty("data").GetProperty("result");
                if (result.GetArrayLength() != 1)
                {
                    return null;
                }

                var raw = result[0].GetProperty("value")[1].GetString();
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? value
                    : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private sealed class TrafficLoop : IDisposable
    {
        private readonly HttpClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, int> _statuses = new();
        private readonly Task _loop;
        private int _total;
        private int _failures;

        private TrafficLoop(string baseUrl)
        {
            _client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
            _loop = Task.Run(RunAsync);
        }

        public static TrafficLoop Start(string baseUrl) => new(baseUrl);

        public async Task<TrafficResult> StopAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                await _cts.CancelAsync();
            }

            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            return new TrafficResult(_total, _failures, new SortedDictionary<int, int>(_statuses));
        }

        public void Dispose()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            _cts.Dispose();
            _client.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref _total);
                try
                {
                    // Readiness exercises the database on every request, so a candidate whose data tier fails
                    // answers 503 rather than a cached success.
                    using var response = await _client.GetAsync("/healthz/ready", _cts.Token);
                    _statuses.AddOrUpdate((int)response.StatusCode, 1, static (_, count) => count + 1);
                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _failures);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
                {
                    // A transport failure through the front door counts as a failed request.
                    Interlocked.Increment(ref _failures);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static class Docker
    {
        public static async Task<ImageIdentity> ResolveImageAsync(string reference)
        {
            var (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", "{{.Id}}|{{join .RepoDigests \",\"}}", reference]);
            if (exitCode != 0)
            {
                await RunCheckedAsync(["pull", "-q", reference]);
                (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", "{{.Id}}|{{join .RepoDigests \",\"}}", reference]);
            }

            exitCode.Should().Be(0, $"image '{reference}' must be resolvable to an immutable id");
            var parts = stdout.Trim().Split('|');
            return new ImageIdentity(
                reference,
                parts[0],
                parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : []);
        }

        public static async Task<string> ContainerAddressAsync(string name)
        {
            var (exitCode, stdout, stderr) = await RunAsync(["inspect", "-f", "{{.NetworkSettings.IPAddress}}", name]);
            var address = stdout.Trim();
            if (exitCode != 0 || string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException($"Could not resolve the address of container '{name}': {stderr.Trim()}");
            }

            return address;
        }

        public static async Task RunCheckedAsync(IReadOnlyList<string> arguments)
        {
            var (exitCode, _, stderr) = await RunAsync(arguments);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"docker {arguments[0]} failed (exit {exitCode.ToString(CultureInfo.InvariantCulture)}): {stderr.Trim()}");
            }
        }

        public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private EnvironmentVariableScope()
        {
        }

        public static EnvironmentVariableScope Apply(IReadOnlyDictionary<string, string?> settings)
        {
            var scope = new EnvironmentVariableScope();
            foreach (var (key, value) in settings)
            {
                scope.Set(key.Replace(":", "__", StringComparison.Ordinal), value);
            }

            return scope;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (key, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            _disposed = true;
        }

        private void Set(string key, string? value)
        {
            _previousValues.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
