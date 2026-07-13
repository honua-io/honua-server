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

    // ---- golden-query body assertions (#2811) ----------------------------

    [Fact]
    public async Task ProbeAsync_BodyContainsAssertionMatches_ReportsNoFailures()
    {
        var probe = new HttpDeployHealthProbe(
            new StubHttpClientFactory(new HttpClient(new FixedBodyHandler(HttpStatusCode.OK, """{"golden":true,"count":42}"""))));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest
            {
                Url = "https://example.com/golden",
                Samples = 2,
                ExpectedBodyContains = "\"count\":42"
            },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Failures.Should().Be(0, "a 200 whose body satisfies the substring assertion is healthy");
    }

    [Fact]
    public async Task ProbeAsync_Http200ButBodyDoesNotMatch_CountsAsFailure()
    {
        // The "healthy but corrupt" release: HTTP 200 with a corrupt payload. Status-only probes miss
        // this; the body assertion catches it and counts every mismatch as a failure.
        var probe = new HttpDeployHealthProbe(
            new StubHttpClientFactory(new HttpClient(new FixedBodyHandler(HttpStatusCode.OK, """{"golden":true,"count":0}"""))));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest
            {
                Url = "https://example.com/golden",
                Samples = 3,
                ExpectedBodyContains = "\"count\":42"
            },
            CancellationToken.None);

        result.Validated.Should().BeTrue();
        result.Failures.Should().Be(3, "a 200 with the wrong body is a corrupt release, not a healthy one");
    }

    [Fact]
    public async Task ProbeAsync_Sha256AssertionMatches_ReportsNoFailures()
    {
        const string body = "golden-payload-v1";
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
        var probe = new HttpDeployHealthProbe(
            new StubHttpClientFactory(new HttpClient(new FixedBodyHandler(HttpStatusCode.OK, body))));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest
            {
                Url = "https://example.com/golden",
                Samples = 1,
                ExpectedBodySha256 = expected
            },
            CancellationToken.None);

        result.Failures.Should().Be(0, "the body hashes to the expected checksum");
    }

    [Fact]
    public async Task ProbeAsync_Sha256AssertionMismatch_CountsAsFailure()
    {
        var probe = new HttpDeployHealthProbe(
            new StubHttpClientFactory(new HttpClient(new FixedBodyHandler(HttpStatusCode.OK, "corrupt-payload"))));

        var result = await probe.ProbeAsync(
            new DeployHealthProbeRequest
            {
                Url = "https://example.com/golden",
                Samples = 2,
                ExpectedBodySha256 = "deadbeef"
            },
            CancellationToken.None);

        result.Failures.Should().Be(2, "a body that does not hash to the expected checksum is a failure");
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

    private sealed class FixedBodyHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
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
