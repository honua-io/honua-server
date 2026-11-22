// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ControlPlaneProbeHttpHandlerTests
{
    public enum NetworkPolicy
    {
        PublicOnly,
        AllowPrivate
    }

    [UnitTheory]
    [InlineData(NetworkPolicy.PublicOnly, "https://127.0.0.1/")]
    [InlineData(NetworkPolicy.PublicOnly, "https://169.254.169.254/")]
    [InlineData(NetworkPolicy.AllowPrivate, "http://169.254.169.254/")]
    [InlineData(NetworkPolicy.AllowPrivate, "http://[fe80::1]/")]
    public async Task ConnectionPolicy_RejectsDisallowedLiteral(NetworkPolicy networkPolicy, string url)
    {
        using var handler = ControlPlaneProbeHttpHandler.Create(() => networkPolicy == NetworkPolicy.AllowPrivate);
        using var client = new HttpClient(handler);
        var connect = async () => await client.GetAsync(url);

        await connect.Should().ThrowAsync<HttpRequestException>();
    }

    [UnitTheory]
    [InlineData(NetworkPolicy.PublicOnly)]
    [InlineData(NetworkPolicy.AllowPrivate)]
    public async Task ConnectionPolicy_RejectsMixedResolution(NetworkPolicy networkPolicy)
    {
        using var handler = ControlPlaneProbeHttpHandler.Create(() => networkPolicy == NetworkPolicy.AllowPrivate,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("169.254.169.254") }));
        using var client = new HttpClient(handler);
        var connect = async () => await client.GetAsync("https://probe.example/");

        await connect.Should().ThrowAsync<HttpRequestException>();
    }

    [UnitTest]
    public async Task PrivateNetworkOptIn_ConnectsToTheValidatedResolution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var resolutions = 0;
        using var handler = ControlPlaneProbeHttpHandler.Create(() => true, (_, _) =>
        {
            resolutions++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });
        using var client = new HttpClient(handler);
        var answer = RespondAsync(listener, timeout.Token);

        using var response = await client.GetAsync($"http://probe.example:{endpoint.Port}/", timeout.Token);
        await answer;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        resolutions.Should().Be(1);
    }

    [UnitTest]
    public async Task ConnectionPolicy_PropagatesCancellationDuringResolution()
    {
        using var timeout = new CancellationTokenSource();
        using var handler = ControlPlaneProbeHttpHandler.Create(() => true, (_, token) =>
        {
            timeout.Cancel();
            return Task.FromCanceled<IPAddress[]>(token);
        });
        using var client = new HttpClient(handler);
        var connect = async () => await client.GetAsync("https://probe.example/", timeout.Token);

        await connect.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task RespondAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
        }

        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), cancellationToken);
    }
}
