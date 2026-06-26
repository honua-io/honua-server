// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Infrastructure.AuditLog.Export;

namespace Honua.Server.Tests.Infrastructure.AuditLog.Export;

/// <summary>
/// Unit tests for <see cref="SyslogAuditSink"/> CEF framing and transport error
/// classification (#2157).
/// </summary>
public sealed class SyslogAuditSinkTests
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

    private static SyslogSinkOptions Options() => new()
    {
        Enabled = true,
        Host = "syslog.example",
        Port = 514,
        Facility = 13,
        Region = "us-east-1",
    };

    [Fact]
    public async Task SendAsync_EmitsCefPayloadWithSyslogPriorityHeader()
    {
        var transport = new CapturingTransport();
        var sink = new SyslogAuditSink(Options(), transport);

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var line = transport.LastMessage.Should().ContainSingle().Subject;
        line.Should().Contain("CEF:0|Honua|Honua Server|");
        line.Should().Contain("act=layer.delete");
        // facility 13 * 8 + warning severity 4 = 108
        line.Should().StartWith("<108>1 ");
    }

    [Fact]
    public async Task SendAsync_SocketFailure_ReturnsRetryable()
    {
        var transport = new ThrowingTransport(new SocketException());
        var sink = new SyslogAuditSink(Options(), transport);

        var result = await sink.SendAsync(new[] { SampleEvent() }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    private sealed class CapturingTransport : ISyslogTransport
    {
        public List<string> LastMessage { get; } = [];

        public Task SendAsync(byte[] datagram, CancellationToken ct)
        {
            LastMessage.Add(Encoding.UTF8.GetString(datagram));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingTransport : ISyslogTransport
    {
        private readonly Exception _exception;

        public ThrowingTransport(Exception exception) => _exception = exception;

        public Task SendAsync(byte[] datagram, CancellationToken ct) => throw _exception;
    }
}
