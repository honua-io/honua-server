// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Infrastructure.Monitoring;

internal static class OpsSelfCheckHttpHandler
{
    public static HttpMessageHandler Create()
    {
        var handler = new PinnedDnsConnector(
            static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken),
            new("The health-check destination is not a public HTTPS address.",
                "Unable to connect to the health-check destination."))
            .CreateHandler(maxConnectionsPerServer: 4);
        handler.UseCookies = false;
        handler.UseProxy = false;
        handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        return handler;
    }
}
