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
        using var client = new HttpClient(handler);
        var sink = new SplunkHecAuditSink(client, Options());

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.LastRequestUri!.AbsolutePath.Should().Be("/services/collector/event");
        handler.LastAuthorization.Should().Be("Splunk secret-token");
    }

    [Fact]
    public async Task SendAsync_Http503_ReturnsRetryable()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var sink = new SplunkHecAuditSink(client, Options());

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_Http400_ReturnsPermanentFailure()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);
        var sink = new SplunkHecAuditSink(client, Options());

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
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
}
