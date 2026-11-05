// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

internal interface ISyslogTransport
{
    Task SendAsync(byte[] datagram, CancellationToken ct);
}

internal sealed class UdpSyslogTransport : ISyslogTransport
{
    private readonly string _host;
    private readonly int _port;

    public UdpSyslogTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task SendAsync(byte[] datagram, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        using var client = new UdpClient();
        await client.SendAsync(datagram, _host, _port, ct).ConfigureAwait(false);
    }
}

internal sealed class TcpSyslogTransport : ISyslogTransport
{
    private readonly string _host;
    private readonly int _port;

    public TcpSyslogTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task SendAsync(byte[] datagram, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(datagram, ct).ConfigureAwait(false);
    }
}
