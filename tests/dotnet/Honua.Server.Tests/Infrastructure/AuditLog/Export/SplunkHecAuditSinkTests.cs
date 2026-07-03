// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Infrastructure.AuditLog.Export;

namespace Honua.Server.Tests.Infrastructure.AuditLog.Export;

/// <summary>
/// Unit tests for <see cref="SplunkHecAuditSink"/> HTTP result classification (#2157).
/// </summary>
public sealed class SplunkHecAuditSinkTests
{
    private static AuditEvent SampleEvent() => new()
    {
        Timestamp = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
        EventType = AuditEventType.Authorization,
        Actor = "user-7",
        ActorType = AuditActorType.UserId,
        ResourceType = "layer",
        ResourceId = "roads",
        Action = "layer.delete",
        Outcome = AuditOutcome.Denied,
        CorrelationId = "corr-xyz",
        Details = "{\"status\":403}",
    };

    private static SplunkHecSinkOptions Options() => new()
    {
        Enabled = true,
        Endpoint = "https://splunk.example:8088/",
        Token = "secret-token",
        SourceType = "honua:audit",
        Region = "us-east-1",
    };

    [Fact]
    public async Task SendAsync_Http200_ReturnsSuccess()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var sink = new SplunkHecAuditSink(new SingleHandlerHttpClientFactory(handler), "splunk", Options());

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.LastRequestUri!.AbsolutePath.Should().Be("/services/collector/event");
        handler.LastAuthorization.Should().Be("Splunk secret-token");
    }

    [Fact]
    public async Task SendAsync_Http503_ReturnsRetryable()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var sink = new SplunkHecAuditSink(new SingleHandlerHttpClientFactory(handler), "splunk", Options());

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_Http400_ReturnsPermanentFailure()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest);
        var sink = new SplunkHecAuditSink(new SingleHandlerHttpClientFactory(handler), "splunk", Options());

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ResolvesClientFromFactoryOnEverySend()
    {
        // PA-081: the sink must not capture a single HttpClient at construction — it should
        // resolve a fresh client from the factory on every send so a rotated/replaced
        // primary handler (e.g. after a DNS change) is observed.
        var handler = new StubHandler(HttpStatusCode.OK);
        var factory = new SingleHandlerHttpClientFactory(handler);
        var sink = new SplunkHecAuditSink(factory, "splunk", Options());

        await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);
        await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        factory.CreateClientCallCount.Should().BeGreaterThanOrEqualTo(2,
            "each send should resolve the named client from the factory rather than reusing one captured at construction");
        factory.LastRequestedName.Should().Be("splunk");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StubHandler(HttpStatusCode status) => _status = status;

        public Uri? LastRequestUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(' ', values)
                : null;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    /// <summary>
    /// Minimal <see cref="IHttpClientFactory"/> test double that hands back a client wired
    /// to a fixed handler on every call, recording how many times (and with what name) it
    /// was asked — used to assert sinks resolve a client per send instead of caching one.
    /// </summary>
    private sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public int CreateClientCallCount { get; private set; }

        public string? LastRequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCallCount++;
            LastRequestedName = name;
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
