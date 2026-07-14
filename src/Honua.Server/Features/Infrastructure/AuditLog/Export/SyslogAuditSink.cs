// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Audit sink that emits each event as an RFC 5424-style syslog line carrying a
/// CEF payload (via <see cref="AuditCefFormatter"/>) over UDP (default) or TCP
/// (#2157).
/// </summary>
internal sealed class SyslogAuditSink : IAuditSink
{
    private const string AppName = "honua-server";
    private const string MessageId = "audit";
    private const int SyslogVersion = 1;

    private readonly SyslogSinkOptions _options;
    private readonly ISyslogTransport _transport;
    private readonly string _hostName;

    /// <summary>
    /// Initializes a new syslog sink.
    /// </summary>
    /// <param name="options">Syslog configuration.</param>
    /// <param name="transport">
    /// Transport seam; when null a default UDP or TCP transport is built from
    /// <paramref name="options"/>.
    /// </param>
    public SyslogAuditSink(SyslogSinkOptions options, ISyslogTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _transport = transport ?? CreateDefaultTransport(options);
        _hostName = Environment.MachineName;
    }

    /// <inheritdoc />
    public string SinkType => "syslog";

    /// <inheritdoc />
    public string? Region => _options.Region;

    /// <inheritdoc />
    public async Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Not a pure map: each iteration sends over the wire and can return early on the
        // first transport failure, so this doesn't reduce cleanly to a '.Select(...)'.
        foreach (var evt in events)
        {
            var frame = BuildFrame(evt);
            try
            {
                await _transport.SendAsync(frame, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex)
            {
                return AuditSinkResult.TransientFailure($"{SinkType} socket error: {ex.Message}");
            }
            catch (IOException ex)
            {
                return AuditSinkResult.TransientFailure($"{SinkType} I/O error: {ex.Message}");
            }
        }

        return AuditSinkResult.Success();
    }

    private static ISyslogTransport CreateDefaultTransport(SyslogSinkOptions options)
        => options.UseTcp
            ? new TcpSyslogTransport(options.Host, options.Port)
            : new UdpSyslogTransport(options.Host, options.Port);

    private byte[] BuildFrame(AuditEvent evt)
    {
        var cef = AuditCefFormatter.Format(ToRecord(evt));
        var severity = Severity(evt.Outcome);
        var priority = (_options.Facility * 8) + severity;
        var timestamp = evt.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // RFC 5424: <PRI>VERSION TIMESTAMP HOSTNAME APP-NAME PROCID MSGID SD MSG
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"<{priority}>{SyslogVersion} {timestamp} {_hostName} {AppName} - {MessageId} - {cef}");

        // Non-transparent framing for TCP; a datagram boundary suffices for UDP,
        // but a trailing newline is harmless and keeps collectors that expect it happy.
        return Encoding.UTF8.GetBytes(line + "\n");
    }

    // RFC 5424 severity: warning (4) for security-relevant denied/failed outcomes,
    // informational (6) otherwise.
    private static int Severity(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Denied => 4,
        AuditOutcome.Failure => 4,
        _ => 6,
    };

    // The CEF formatter consumes the persisted read model; export operates on the
    // write-side event which carries no row id, so a synthetic 0 id is used.
    private static AuditEventRecord ToRecord(AuditEvent evt) => new()
    {
        AuditId = 0,
        Timestamp = evt.Timestamp,
        EventType = evt.EventType,
        Actor = evt.Actor,
        ActorType = evt.ActorType,
        ResourceType = evt.ResourceType,
        ResourceId = evt.ResourceId,
        Action = evt.Action,
        Outcome = evt.Outcome,
        CorrelationId = evt.CorrelationId,
        RemoteIp = evt.RemoteIp,
        UserAgent = evt.UserAgent,
        Details = evt.Details,
    };
}
