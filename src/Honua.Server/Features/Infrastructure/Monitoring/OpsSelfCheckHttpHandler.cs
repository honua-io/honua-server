// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Infrastructure.Monitoring;

internal static class OpsSelfCheckHttpHandler
{
    public static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectAsync
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var uri = new UriBuilder(Uri.UriSchemeHttps, context.DnsEndPoint.Host, context.DnsEndPoint.Port).Uri;
        var validation = await OutboundHttpUrlValidator.ValidateAsync(uri.AbsoluteUri,
            (_, _) => Task.FromResult(addresses), cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new HttpRequestException("The health-check destination is not a public HTTPS address.");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
