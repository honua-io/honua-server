// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Honua.Server.Features.Admin.Federation;
using Honua.Server.Features.Admin.Federation.Connectors;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Features.Admin.Federation;

public sealed class FederationHttpTransportTests
{
    [UnitTest]
    public async Task AddFederationServices_LoopbackDestination_RejectsWithoutConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var services = new ServiceCollection();
        services.AddFederationServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(HttpGeoJsonFederatedSourceConnector.HttpClientName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://127.0.0.1:{endpoint.Port}/", cancellation.Token));

        Assert.Contains("not a public", exception.ToString(), StringComparison.Ordinal);
        Assert.False(listener.Pending());
    }
}
