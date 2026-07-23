// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// ADR-0060 substrate-neutral SERVING lane (#2166, #2457). Proves the single-host, zero-cloud
/// (containers only — no Kubernetes, no cloud) zero-downtime deploy / rolling-upgrade / rollback story
/// end-to-end by driving the REAL <see cref="YarpRollingDeployBackend"/> against real
/// <c>docker</c> replica containers, with the REAL <see cref="YarpInMemoryProxyStateSwapper"/> wired to
/// a REAL embedded-YARP <see cref="InMemoryConfigProvider"/> fronting the traffic. A continuous request
/// loop through the proxied endpoint measures the zero-downtime property across the atomic cutover.
///
/// Every test <c>[SkippableFact]</c>-skips (never fails) when Docker is unavailable, mirroring the
/// existing kind/LocalStack lanes.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class LocalSubstrateRollingDeployTests : IClassFixture<LocalSubstrateDockerFixture>
{
    private readonly LocalSubstrateDockerFixture _docker;

    public LocalSubstrateRollingDeployTests(LocalSubstrateDockerFixture docker)
    {
        _docker = docker;
    }

    [SkippableFact]
    public async Task RollingDeploy_HealthGatedPromotion_FlipsTrafficWithZeroDowntime()
    {
        Skip.IfNot(_docker.Available, "Docker is not available for the local-substrate serving lane.");

        await using var env = await RollingDeployEnvironment.StartAsync(_docker);

        // A continuous request loop spans the entire deploy: start it while v1 is serving so it
        // observes every request through the standby launch, health gate, and cutover.
        using var loop = env.StartRequestLoop();

        var operation = env.CreateDeployOperation();

        // 1. Submit: the backend launches the v2 standby replica; the v1 active replica keeps serving.
        var submission = await env.Backend.StartAsync(operation);
        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();
        operation = operation with { ProviderOperationId = submission.ProviderOperationId };

        // 2. Observe until the health-gated standby is ready for promotion.
        var observation = await PollObservationAsync(
            () => env.Backend.ObserveAsync(operation),
            o => o.PromotionRecommended,
            TimeSpan.FromSeconds(90));
        observation.PromotionRecommended.Should().BeTrue("the v2 standby replica must pass its health gate");

        // 3. Promote: atomic proxy destination swap to v2, then drain and stop the old v1 replica.
        var promote = await env.Backend.PromoteAsync(operation);
        promote.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        promote.ObservedRevision.Should().Be(_docker.V2Image);

        // Let the loop capture post-cutover v2 responses before stopping it.
        await Task.Delay(500);
        var result = await loop.StopAsync();

        // Zero-downtime proof: not one request through the proxy failed across the cutover, and the
        // observed body flipped v1 -> v2.
        result.Failures.Should().Be(0, "the atomic cutover keeps the old replica serving until the new one is healthy");
        result.Total.Should().BeGreaterThan(0);
        result.SawV1.Should().BeTrue("traffic served the previous revision before promotion");
        result.SawV2.Should().BeTrue("traffic served the new revision after promotion");
        result.LastBody.Should().Contain(LocalSubstrateDockerFixture.V2Marker);
    }

    [SkippableFact]
    public async Task RollingDeploy_RollbackBeforeCutover_KeepsPreviousServingWithZeroDowntime()
    {
        Skip.IfNot(_docker.Available, "Docker is not available for the local-substrate serving lane.");

        await using var env = await RollingDeployEnvironment.StartAsync(_docker);
        using var loop = env.StartRequestLoop();

        var operation = env.CreateDeployOperation();

        var submission = await env.Backend.StartAsync(operation);
        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        operation = operation with { ProviderOperationId = submission.ProviderOperationId };

        // Health-gate the standby, then roll back BEFORE the cutover.
        var observation = await PollObservationAsync(
            () => env.Backend.ObserveAsync(operation),
            o => o.PromotionRecommended,
            TimeSpan.FromSeconds(90));
        observation.PromotionRecommended.Should().BeTrue();

        // Pre-cutover rollback: the proxy was never repointed, so the old replica was never touched.
        var rollback = await env.Backend.RollbackAsync(operation);
        rollback.Status.Should().Be(WorkflowOperationStatus.RolledBack);

        await Task.Delay(500);
        var result = await loop.StopAsync();

        // The v1 replica kept serving the whole time: zero failures and traffic never flipped to v2.
        result.Failures.Should().Be(0, "a pre-cutover rollback never disturbs the serving replica");
        result.SawV1.Should().BeTrue();
        result.SawV2.Should().BeFalse("traffic must never have reached the discarded standby before promotion");
        result.LastBody.Should().Contain(LocalSubstrateDockerFixture.V1Marker);
    }

    [SkippableFact]
    public async Task RollingDeploy_RollbackAfterCutover_RepointsToPreviousAndSettlesRolledBack()
    {
        Skip.IfNot(_docker.Available, "Docker is not available for the local-substrate serving lane.");

        await using var env = await RollingDeployEnvironment.StartAsync(_docker);

        var operation = env.CreateDeployOperation();

        var submission = await env.Backend.StartAsync(operation);
        operation = operation with { ProviderOperationId = submission.ProviderOperationId };

        var observation = await PollObservationAsync(
            () => env.Backend.ObserveAsync(operation),
            o => o.PromotionRecommended,
            TimeSpan.FromSeconds(90));
        observation.PromotionRecommended.Should().BeTrue();

        // Promote to v2 (this stops the old v1 replica), then request a post-cutover rollback.
        var promote = await env.Backend.PromoteAsync(operation);
        promote.Status.Should().Be(WorkflowOperationStatus.Succeeded);

        var rollback = await env.Backend.RollbackAsync(operation);
        rollback.Status.Should().Be(WorkflowOperationStatus.RollbackRequested,
            "a post-cutover rollback repoints the proxy at the previous revision and drains the failed one");

        // The proxy was repointed back at the previous (active-port) replica address.
        env.Swapper.ActiveDestinationAddress.Should().Be(env.ActiveAddress);

        // The rollback settles once the failed standby replica has drained and gone.
        var rolledBack = operation with { Status = WorkflowOperationStatus.RollbackRequested };
        var settled = await PollObservationAsync(
            () => env.Backend.ObserveAsync(rolledBack),
            o => o.Status == WorkflowOperationStatus.RolledBack,
            TimeSpan.FromSeconds(60));
        settled.Status.Should().Be(WorkflowOperationStatus.RolledBack);

        // The relaunched previous revision is serving again through the proxy.
        var servingV1 = await LocalSubstrateDockerFixture.WaitForBodyAsync(
            env.ProxyBaseUrl + "/", LocalSubstrateDockerFixture.V1Marker, TimeSpan.FromSeconds(30));
        servingV1.Should().BeTrue("the previous revision is relaunched and fronted by the proxy after rollback");
    }

    private static async Task<DeployObservation> PollObservationAsync(
        Func<Task<DeployObservation>> observe,
        Func<DeployObservation, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        DeployObservation last = new() { Status = WorkflowOperationStatus.Reconciling };
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await observe();
            if (last.Status == WorkflowOperationStatus.Failed)
            {
                throw new InvalidOperationException($"Rolling deploy observation failed: {last.Message}");
            }

            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"Rolling deploy did not satisfy the expected condition within {timeout}. Last status: {last.Status} ({last.Message}).");
    }

    /// <summary>
    /// One self-hosted rolling-deploy scenario's live environment: the co-located v1 active replica,
    /// the running embedded-YARP proxy fronting it, the real proxy-swap seam, and the backend under
    /// test. Owns cleanup of every container it launched plus the proxy host.
    /// </summary>
    private sealed class RollingDeployEnvironment : IAsyncDisposable
    {
        private readonly LocalSubstrateDockerFixture _docker;
        private readonly WebApplication _proxy;
        private readonly SelfHostedDeployOptions _options;

        private RollingDeployEnvironment(
            LocalSubstrateDockerFixture docker,
            WebApplication proxy,
            SelfHostedDeployOptions options,
            YarpRollingDeployBackend backend,
            YarpInMemoryProxyStateSwapper swapper,
            string targetId,
            string proxyBaseUrl)
        {
            _docker = docker;
            _proxy = proxy;
            _options = options;
            Backend = backend;
            Swapper = swapper;
            TargetId = targetId;
            ProxyBaseUrl = proxyBaseUrl;
        }

        public YarpRollingDeployBackend Backend { get; }

        public YarpInMemoryProxyStateSwapper Swapper { get; }

        public string TargetId { get; }

        public string ProxyBaseUrl { get; }

        public string ActiveAddress =>
            $"http://{_options.Host}:{_options.ActivePort.ToString(CultureInfo.InvariantCulture)}/";

        public static async Task<RollingDeployEnvironment> StartAsync(LocalSubstrateDockerFixture docker)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var targetId = $"honua-ci-ls-{suffix}";
            var activePort = LocalSubstrateDockerFixture.GetFreeTcpPort();
            int standbyPort;
            do
            {
                standbyPort = LocalSubstrateDockerFixture.GetFreeTcpPort();
            }
            while (standbyPort == activePort);

            var options = new SelfHostedDeployOptions
            {
                Enabled = true,
                ContainerRuntime = docker.RuntimeExecutable,
                ContainerNamePrefix = $"honua-ci-ls-{suffix}",
                Host = "127.0.0.1",
                // busybox httpd answers 200 at "/", which doubles as the replica health signal.
                HealthPath = "/",
                ActivePort = activePort,
                StandbyPort = standbyPort,
                ContainerPort = LocalSubstrateDockerFixture.ReplicaContainerPort,
                HealthProbeSamples = 3,
                HealthProbeTimeoutSeconds = 3,
                HealthProbeExpectedStatusCode = 200,
                // Short drain so the scenario stays well under the lane's runtime budget.
                DrainDelaySeconds = 2
            };

            // 1. Launch the co-located v1 active replica the rollout will replace.
            await docker.Runtime.RunAsync(
                new ContainerRunRequest
                {
                    Executable = options.ContainerRuntime,
                    Image = docker.V1Image,
                    ContainerName = $"{options.ContainerNamePrefix}-{activePort.ToString(CultureInfo.InvariantCulture)}",
                    HostPort = activePort,
                    ContainerPort = options.ContainerPort,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [YarpRollingDeployBackend.LabelTarget] = targetId,
                        [YarpRollingDeployBackend.LabelRole] = YarpRollingDeployBackend.RoleActive,
                        [YarpRollingDeployBackend.LabelRevision] = docker.V1Image
                    }
                },
                CancellationToken.None);

            var v1Ready = await LocalSubstrateDockerFixture.WaitForBodyAsync(
                $"http://127.0.0.1:{activePort.ToString(CultureInfo.InvariantCulture)}/",
                LocalSubstrateDockerFixture.V1Marker,
                TimeSpan.FromSeconds(30));
            if (!v1Ready)
            {
                await docker.CleanupTargetAsync(targetId);
                throw new InvalidOperationException("The v1 active replica did not become ready.");
            }

            // 2. Stand up the embedded-YARP proxy pointed at v1, and take the REAL swap seam over its
            //    live InMemoryConfigProvider so cutover is an atomic switch on the running proxy.
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var initialDestination = YarpInMemoryProxyStateSwapper.InitialActiveAddress(options);
            builder.Services.AddReverseProxy().LoadFromMemory(
                SelfHostedProxyConfig.BuildRoutes(options),
                SelfHostedProxyConfig.BuildClusters(options, initialDestination));

            var proxy = builder.Build();
            proxy.MapReverseProxy();
            await proxy.StartAsync();

            var configProvider = (InMemoryConfigProvider)proxy.Services.GetRequiredService<IProxyConfigProvider>();
            var swapper = new YarpInMemoryProxyStateSwapper(configProvider, docker.Runtime, Options.Create(options));

            var backend = new YarpRollingDeployBackend(
                docker.Runtime,
                swapper,
                docker.HealthProbe,
                Options.Create(options),
                NullLogger<YarpRollingDeployBackend>.Instance);

            var proxyBaseUrl = proxy.Urls.First().TrimEnd('/');

            // Confirm the proxy actually fronts v1 before any scenario begins.
            var proxyServesV1 = await LocalSubstrateDockerFixture.WaitForBodyAsync(
                proxyBaseUrl + "/", LocalSubstrateDockerFixture.V1Marker, TimeSpan.FromSeconds(15));
            if (!proxyServesV1)
            {
                await proxy.StopAsync();
                await proxy.DisposeAsync();
                await docker.CleanupTargetAsync(targetId);
                throw new InvalidOperationException("The proxy did not front the v1 replica.");
            }

            return new RollingDeployEnvironment(docker, proxy, options, backend, swapper, targetId, proxyBaseUrl);
        }

        public WorkflowOperationRecord CreateDeployOperation()
        {
            var now = DateTimeOffset.UtcNow;
            var spec = new DeployOperationSpec
            {
                TargetId = TargetId,
                TargetKind = DeployTargetKind.SelfHostedRolling,
                Backend = YarpRollingDeployBackend.AdapterBackendName,
                Environment = "local",
                TargetName = "honua-serving",
                DesiredRevision = _docker.V2Image,
                CurrentRevision = _docker.V1Image,
                ArtifactReference = _docker.V2Image,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SelfHostedDeployParameterKeys.Image] = _docker.V2Image,
                    [SelfHostedDeployParameterKeys.ActivePort] = _options.ActivePort.ToString(CultureInfo.InvariantCulture),
                    [SelfHostedDeployParameterKeys.StandbyPort] = _options.StandbyPort.ToString(CultureInfo.InvariantCulture),
                    [SelfHostedDeployParameterKeys.ContainerPort] = _options.ContainerPort.ToString(CultureInfo.InvariantCulture)
                }
            };

            return new WorkflowOperationRecord
            {
                OperationId = $"ls-deploy-{Guid.NewGuid():N}"[..20],
                Kind = WorkflowOperationKind.Deploy,
                Status = WorkflowOperationStatus.Submitted,
                CreatedAt = now,
                UpdatedAt = now,
                Deploy = spec
            };
        }

        public RequestLoop StartRequestLoop() => RequestLoop.Start(ProxyBaseUrl + "/");

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _proxy.StopAsync();
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Best-effort proxy shutdown.
            }

            await _proxy.DisposeAsync();
            await _docker.CleanupTargetAsync(TargetId);
        }
    }

    /// <summary>
    /// Continuous request generator against the proxied endpoint. Records failures and which revision
    /// bodies were observed so a scenario can assert the zero-downtime and traffic-flip properties.
    /// </summary>
    private sealed class RequestLoop : IDisposable
    {
        private readonly HttpClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _total;
        private int _failures;
        private volatile bool _sawV1;
        private volatile bool _sawV2;
        private volatile string? _lastBody;

        private RequestLoop(string url)
        {
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            _loop = Task.Run(() => RunAsync(url));
        }

        public static RequestLoop Start(string url) => new(url);

        public async Task<RequestLoopResult> StopAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            return new RequestLoopResult(_total, _failures, _sawV1, _sawV2, _lastBody);
        }

        private async Task RunAsync(string url)
        {
            while (!_cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref _total);
                try
                {
                    using var response = await _client.GetAsync(url, _cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _failures);
                    }
                    else
                    {
                        var body = (await response.Content.ReadAsStringAsync(_cts.Token)).Trim();
                        _lastBody = body;
                        if (body.Contains(LocalSubstrateDockerFixture.V1Marker, StringComparison.Ordinal))
                        {
                            _sawV1 = true;
                        }

                        if (body.Contains(LocalSubstrateDockerFixture.V2Marker, StringComparison.Ordinal))
                        {
                            _sawV2 = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // Loop shutdown — not a served-request failure.
                    break;
                }
                catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
                {
                    // Any other transport/deserialization error during rolling-deploy load generation
                    // just counts as a failed request; cancellation is already rethrown above.
                    Interlocked.Increment(ref _failures);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
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
    }

    private sealed record RequestLoopResult(int Total, int Failures, bool SawV1, bool SawV2, string? LastBody);
}
