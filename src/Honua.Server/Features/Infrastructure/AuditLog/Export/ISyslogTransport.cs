// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Transport seam for <see cref="SyslogAuditSink"/> so the framing/formatting
/// logic can be unit-tested without opening a real socket.
/// </summary>
internal interface ISyslogTransport
{
    /// <summary>
    /// Sends one already-framed syslog message.
    /// </summary>
    /// <param name="datagram">The UTF-8 encoded, framed syslog message.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(byte[] datagram, CancellationToken ct);
}

/// <summary>
/// Default UDP <see cref="ISyslogTransport"/>. Each message is sent as its own
/// datagram to the configured host/port.
/// </summary>
internal sealed class UdpSyslogTransport : ISyslogTransport
{
    private readonly string _host;
    private readonly int _port;

    /// <summary>Initializes a new UDP transport.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="port">Collector port.</param>
    public UdpSyslogTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <inheritdoc />
    public async Task SendAsync(byte[] datagram, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        using var client = new UdpClient();
        await client.SendAsync(datagram, _host, _port, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Default TCP <see cref="ISyslogTransport"/> using non-transparent (newline)
/// framing per RFC 6587. A fresh connection is opened per batch flush.
/// </summary>
internal sealed class TcpSyslogTransport : ISyslogTransport
{
    private readonly string _host;
    private readonly int _port;

    /// <summary>Initializes a new TCP transport.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="port">Collector port.</param>
    public TcpSyslogTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <inheritdoc />
    public async Task SendAsync(byte[] datagram, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(datagram, ct).ConfigureAwait(false);
    }
}
