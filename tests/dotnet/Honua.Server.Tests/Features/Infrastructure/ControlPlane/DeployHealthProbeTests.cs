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
        using var factory = StubFactory(HttpStatusCode.OK);
        var probe = new HttpDeployHealthProbe(factory);

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
        using var factory = StubFactory(HttpStatusCode.ServiceUnavailable);
        var probe = new HttpDeployHealthProbe(factory);

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
        using var httpClient = new HttpClient(new ThrowingHandler());
        using var factory = new StubHttpClientFactory(httpClient);
        var probe = new HttpDeployHealthProbe(factory);

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
        using var httpClient = new HttpClient(new CountingHandler(() => sendCount++));
        using var factory = new StubHttpClientFactory(httpClient);
        var probe = new HttpDeployHealthProbe(factory);

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest { Url = "http://localhost:8080/healthz/ready", Samples = 3 },
            CancellationToken.None);

        result.Validated.Should().BeFalse("a non-HTTPS loopback URL fails outbound-URL validation");
        result.Attempts.Should().Be(0);
        sendCount.Should().Be(0, "a rejected URL must never hit the network");
    }

    [Fact]
    public async Task ProbeGoldenQueryAsync_BodyContainsToken_Matches()
    {
        using var factory = BodyFactory(HttpStatusCode.OK, "{\"marker\":\"GOLDEN-OK\"}");
        var probe = new HttpDeployHealthProbe(factory);

        var result = await probe.ProbeGoldenQueryAsync(
            new DeployGoldenQueryRequest { Url = "https://example.com/probe", ExpectedBodyContains = "GOLDEN-OK" },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Matched.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeGoldenQueryAsync_HealthyStatusButCorruptBody_DoesNotMatch()
    {
        // The heart of #2811: status 200 but a wrong/garbled body must be a mismatch (correctness gate).
        using var factory = BodyFactory(HttpStatusCode.OK, "<html>error page</html>");
        var probe = new HttpDeployHealthProbe(factory);

        var result = await probe.ProbeGoldenQueryAsync(
            new DeployGoldenQueryRequest { Url = "https://example.com/probe", ExpectedBodyContains = "GOLDEN-OK" },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Matched.Should().BeFalse("a 200 response with the wrong body is corrupt, not correct");
        result.Detail.Should().Contain("golden token");
    }

    [Fact]
    public async Task ProbeGoldenQueryAsync_ChecksumMismatch_DoesNotMatch()
    {
        using var factory = BodyFactory(HttpStatusCode.OK, "actual-body");
        var probe = new HttpDeployHealthProbe(factory);

        var result = await probe.ProbeGoldenQueryAsync(
            new DeployGoldenQueryRequest
            {
                Url = "https://example.com/probe",
                ExpectedSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            },
            CancellationToken.None);

        result.Matched.Should().BeFalse("the body checksum does not match the expected digest");
    }

    [Fact]
    public async Task ProbeGoldenQueryAsync_PrivateOrNonHttpsUrl_IsNotValidated()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new CountingHandler(() => sendCount++));
        using var factory = new StubHttpClientFactory(httpClient);
        var probe = new HttpDeployHealthProbe(factory);

        var result = await probe.ProbeGoldenQueryAsync(
            new DeployGoldenQueryRequest { Url = "http://localhost:8080/probe", ExpectedBodyContains = "x" },
            CancellationToken.None);

        result.Validated.Should().BeFalse("a non-HTTPS loopback URL fails outbound-URL validation");
        result.Matched.Should().BeFalse();
        sendCount.Should().Be(0, "a rejected URL must never hit the network");
    }

    private static StubHttpClientFactory StubFactory(HttpStatusCode status)
        => new(new HttpClient(new FixedStatusHandler(status)));

    private static StubHttpClientFactory BodyFactory(HttpStatusCode status, string body)
        => new(new HttpClient(new FixedBodyHandler(status, body)));

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory, IDisposable
    {
        public HttpClient CreateClient(string name) => client;

        public void Dispose() => client.Dispose();
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            // Ownership of the response transfers to the caller, which disposes it via its
            // own `using var response = ...` (HttpDeployHealthProbe.cs).
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class FixedBodyHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            // Ownership of the response transfers to the caller, which disposes it via its
            // own `using var response = ...` (HttpDeployHealthProbe.cs).
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
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
            // Ownership of the response transfers to the caller, which disposes it via its
            // own `using var response = ...` (HttpDeployHealthProbe.cs).
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
