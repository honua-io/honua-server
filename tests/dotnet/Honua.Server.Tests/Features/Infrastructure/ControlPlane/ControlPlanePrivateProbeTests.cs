// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ControlPlanePrivateProbeTests
{
    [UnitTheory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RegisteredPrivateProbes_PreserveResponseVerdicts(HttpStatusCode statusCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/probe";
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ControlPlaneOptions>(options => options.AllowPrivateNetworkProbes = true);
        services.AddControlPlaneHttpClients();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var health = new HttpDeployHealthProbe(factory, provider.GetRequiredService<IOptionsMonitor<ControlPlaneOptions>>());
        var restore = new HttpServingCustomerProbe(factory, Options.Create(new RestoreCellOptions { CustomerProbeUrl = url }));
        var server = RespondAsync(listener, statusCode, timeout.Token);

        var readiness = await health.ProbeAsync(new DeployHealthProbeRequest { Url = url, Samples = 1 }, timeout.Token);
        var golden = await health.ProbeGoldenQueryAsync(
            new DeployGoldenQueryRequest { Url = url, ExpectedBodyContains = "ready" }, timeout.Token);
        var serving = await restore.SucceededAsync(timeout.Token);
        await server;

        var successful = statusCode == HttpStatusCode.OK;
        readiness.Validated.Should().BeTrue();
        readiness.Attempts.Should().Be(1);
        readiness.Failures.Should().Be(successful ? 0 : 1);
        golden.Validated.Should().BeTrue();
        golden.Matched.Should().Be(successful);
        serving.Should().Be(successful);
    }

    private static async Task RespondAsync(TcpListener listener, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        for (var request = 0; request < 3; request++)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
                // Drain request headers before writing the response.
            }

            var status = statusCode == HttpStatusCode.OK ? "200 OK" : "503 Service Unavailable";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 5\r\nConnection: close\r\n\r\nready");
            await stream.WriteAsync(response, cancellationToken);
        }
    }
}
