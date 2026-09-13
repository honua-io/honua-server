// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Isolation between the control plane's telemetry client and its candidate probes (honua-server#4617).
/// The probes used to share the telemetry client, whose retry and circuit breaker suit a metrics backend.
/// On a real rollout of the 9f2f16a server image, a candidate whose readiness answered 503 opened that
/// breaker, so the Prometheus queries behind the telemetry gate failed with BrokenCircuitException while
/// Prometheus itself reported a 0.79 error rate. The gate recovered only by missing-evidence rollback.
/// </summary>
public sealed class ControlPlaneHttpClientsTests
{
    [Fact]
    public async Task FailingCandidateProbes_AreSentOncePerSample_AndCannotBlindTheTelemetryClient()
    {
        var handler = new CandidateAndPrometheusHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControlPlaneHttpClients();
        services.AddHttpClient(ControlPlaneHttpClients.Probe).ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient(ControlPlaneHttpClients.Telemetry).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var probe = new HttpLocalReplicaHealthProbe(factory);

        for (var round = 0; round < 5; round++)
        {
            var result = await probe.ProbeAsync(
                "http://127.0.0.1:18081/healthz/ready",
                samples: 3,
                timeoutSeconds: 5,
                expectedStatusCode: 200,
                CancellationToken.None);
            result.Failures.Should().Be(3);
        }

        handler.RequestsTo("/healthz/ready").Should().Be(15, "each probe sample is exactly one request; a retry would hide how the candidate answers");

        using var telemetry = factory.CreateClient(ControlPlaneHttpClients.Telemetry);
        using var response = await telemetry.GetAsync(new Uri("http://127.0.0.1:9090/api/v1/query?query=up"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a failing candidate must not open a circuit breaker in front of the metrics backend");
    }

    [Fact]
    public async Task DeployHealthProbe_ReadinessAndGoldenQuery_UseTheProbeClient()
    {
        var factory = new RecordingHttpClientFactory(new CandidateAndPrometheusHandler());
        var probe = new HttpDeployHealthProbe(factory);

        await probe.ProbeAsync(
            new DeployHealthProbeRequest { Url = "https://example.com/healthz/ready", Samples = 1 },
            CancellationToken.None);
        await probe.ProbeGoldenQueryAsync(
            new DeployGoldenQueryRequest { Url = "https://example.com/api/v1/query?query=up", ExpectedBodyContains = "success" },
            CancellationToken.None);

        factory.Names.Should().HaveCount(2).And.OnlyContain(name => name == ControlPlaneHttpClients.Probe);
    }

    [Fact]
    public async Task PrometheusProvider_UsesTheTelemetryClient()
    {
        var factory = new RecordingHttpClientFactory(new CandidateAndPrometheusHandler());
        var provider = new PrometheusDeployTelemetryProviderEvaluator(factory);

        await provider.ReadAsync(
            new DeployTelemetryPolicyDescriptor { ConnectionId = "prod-prom", ErrorRateQuery = "sum(up)", ErrorRateThreshold = 0.05 },
            new DeployTelemetryConnectionDescriptor { ConnectionId = "prod-prom", Provider = "prometheus", BaseUrl = "https://example.com", TimeoutSeconds = 2 },
            CancellationToken.None);

        factory.Names.Should().NotBeEmpty().And.OnlyContain(name => name == ControlPlaneHttpClients.Telemetry);
    }

    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public ConcurrentQueue<string> Names { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Names.Enqueue(name);
            return new Honua.TestKit.CallerOwnedHttpClient(handler);
        }
    }

    /// <summary>
    /// A candidate whose readiness answers 503 on every request, next to a Prometheus that answers queries.
    /// </summary>
    private sealed class CandidateAndPrometheusHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);

        public int RequestsTo(string path) => _requests.GetValueOrDefault(path);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _requests.AddOrUpdate(path, 1, static (_, count) => count + 1);
            if (path.StartsWith("/healthz", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Not Ready", Encoding.UTF8, "text/plain")
                });
            }

            var observedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"vector\",\"result\":[{{\"metric\":{{}},\"value\":[{observedAt},\"1\"]}}]}}}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
