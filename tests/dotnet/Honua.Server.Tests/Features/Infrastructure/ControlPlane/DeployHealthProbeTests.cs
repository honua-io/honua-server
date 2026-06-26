// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Coverage for <see cref="HttpDeployHealthProbe"/> (#1849): the synthetic <c>/healthz/ready</c>
/// signal that promotes "unhealthy" to a first-class rollback trigger. The probe issues N sequential
/// HTTPS GETs, counts how many do not return the expected status (or fail outright), and refuses to
/// run against a non-HTTPS / private destination (SSRF protection shared with the metrics gate).
/// </summary>
public sealed class DeployHealthProbeTests
{
    [Fact]
    public async Task ProbeAsync_AllChecksReturnExpectedStatus_ReportsNoFailures()
    {
        var probe = new HttpDeployHealthProbe(StubFactory(HttpStatusCode.OK));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest { Url = "https://example.com/healthz/ready", Samples = 3 },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Attempts.Should().Be(3);
        result.Failures.Should().Be(0);
    }

    [Fact]
    public async Task ProbeAsync_AllChecksUnhealthy_CountsEveryFailure()
    {
        var probe = new HttpDeployHealthProbe(StubFactory(HttpStatusCode.ServiceUnavailable));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest { Url = "https://example.com/healthz/ready", Samples = 4 },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Attempts.Should().Be(4);
        result.Failures.Should().Be(4, "every 503 response is an unhealthy check");
    }

    [Fact]
    public async Task ProbeAsync_TransportFailure_CountsAsUnhealthy()
    {
        var probe = new HttpDeployHealthProbe(
            new StubHttpClientFactory(new HttpClient(new ThrowingHandler())));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest { Url = "https://example.com/healthz/ready", Samples = 2 },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Failures.Should().Be(2, "a connection that throws is an unhealthy target, not a skipped check");
    }

    [Fact]
    public async Task ProbeAsync_PrivateOrNonHttpsUrl_IsNotValidated_AndDoesNotProbe()
    {
        var sendCount = 0;
        var probe = new HttpDeployHealthProbe(
            new StubHttpClientFactory(new HttpClient(new CountingHandler(() => sendCount++))));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest { Url = "http://localhost:8080/healthz/ready", Samples = 3 },
            CancellationToken.None);

        result.Validated.Should().BeFalse("a non-HTTPS loopback URL fails outbound-URL validation");
        result.Attempts.Should().Be(0);
        sendCount.Should().Be(0, "a rejected URL must never hit the network");
    }

    private static StubHttpClientFactory StubFactory(HttpStatusCode status)
        => new(new HttpClient(new FixedStatusHandler(status)));

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
