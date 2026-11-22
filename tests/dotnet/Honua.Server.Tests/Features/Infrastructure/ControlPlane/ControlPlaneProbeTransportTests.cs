// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[CollectionDefinition("ControlPlaneProbeTransport", DisableParallelization = true)]
public sealed class ControlPlaneProbeTransportTestGroup;

[Collection("ControlPlaneProbeTransport")]
public sealed class ControlPlaneProbeTransportTests : IDisposable
{
    private readonly string? _previousPrivateNetworkSetting =
        Environment.GetEnvironmentVariable(DeployProbeUrlPolicy.AllowPrivateNetworksEnvVar);

    public ControlPlaneProbeTransportTests()
        => Environment.SetEnvironmentVariable(DeployProbeUrlPolicy.AllowPrivateNetworksEnvVar, "false");

    public void Dispose()
        => Environment.SetEnvironmentVariable(DeployProbeUrlPolicy.AllowPrivateNetworksEnvVar, _previousPrivateNetworkSetting);

    [Fact]
    public void AddControlPlaneHttpClients_ProbeTransport_UsesConfiguredConnectionPolicy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControlPlaneHttpClients();
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(ControlPlaneHttpClients.Probe);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var transport = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        transport.AllowAutoRedirect.Should().BeFalse();
        transport.UseProxy.Should().BeFalse();
        transport.ConnectCallback.Should().NotBeNull();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("")]
    public async Task SendAsync_DestinationOutsideConfiguredPolicy_IsRejected(string address)
    {
        using var handler = new DeployProbeTransport((_, _) => Task.FromResult(
            address.Length == 0 ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(address) }))
            .CreateHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var send = () => client.GetAsync("https://probe.example/healthz");

        await send.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*outside the configured connection policy*");
    }

    [Theory]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("64:ff9b::a9fe:101")]
    public async Task SendAsync_PrivateNetworkConfiguration_RetainsDestinationRestrictions(string address)
    {
        Environment.SetEnvironmentVariable(DeployProbeUrlPolicy.AllowPrivateNetworksEnvVar, "true");
        using var handler = new DeployProbeTransport((_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }))
            .CreateHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var send = () => client.GetAsync("http://probe.example/healthz");

        await send.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*outside the configured connection policy*");
    }

    [Fact]
    public async Task SendAsync_AfterPolicyValidation_UsesCurrentResolution()
    {
        var resolutions = 0;
        Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(new[] { ++resolutions <= 2 ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback });
        const string url = "https://probe.example/healthz";
        var validation = await DeployProbeUrlPolicy.ValidateAsync(url, false, ResolveAsync, CancellationToken.None);
        validation.IsValid.Should().BeTrue();
        using var handler = new DeployProbeTransport(ResolveAsync).CreateHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

        var send = () => client.GetAsync(url);

        await send.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*outside the configured connection policy*");
        resolutions.Should().Be(3);
    }

    [Fact]
    public async Task SendAsync_PrivateNetworkConfiguration_ConnectsToResolvedDestinationOnce()
    {
        Environment.SetEnvironmentVariable(DeployProbeUrlPolicy.AllowPrivateNetworksEnvVar, "true");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = ServeResponseAsync(listener, cancellation.Token);
        var resolutions = 0;
        using var handler = new DeployProbeTransport((_, _) =>
        {
            resolutions++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        }).CreateHandler();
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync($"http://probe.example:{port}/healthz", cancellation.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resolutions.Should().Be(2);
        await serve;
    }

    [Fact]
    public async Task AddControlPlaneHttpClients_PrivateNetworkOption_AppliesToConnections()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ControlPlaneOptions>(options => options.AllowPrivateNetworkProbes = true);
        services.AddControlPlaneHttpClients();
        using var provider = services.BuildServiceProvider();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = ServeResponseAsync(listener, cancellation.Token);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ControlPlaneHttpClients.Probe);

        using var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz", cancellation.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await serve;
    }

    [Fact]
    public async Task SendAsync_ReusedConnection_AppliesRequestPolicy()
    {
        Environment.SetEnvironmentVariable(DeployProbeUrlPolicy.AllowPrivateNetworksEnvVar, "true");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = ServePersistentResponseAsync(listener, cancellation.Token);
        using var handler = new DeployProbeTransport((_, _) => Task.FromResult(new[] { IPAddress.Loopback }))
            .CreateHandler();
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync($"http://probe.example:{port}/healthz", cancellation.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var send = () => client.GetAsync($"http://user:password@probe.example:{port}/healthz", cancellation.Token);

        await send.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*outside the configured connection policy*");
        client.Dispose();
        (await serve).Should().BeNull();
    }

    private static async Task<string?> ServePersistentResponseAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
            // Consume the request headers before returning the response.
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), cancellationToken);
        return await reader.ReadLineAsync(cancellationToken);
    }

    private static async Task ServeResponseAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
            // Consume the request headers before returning the response.
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\nLocation: http://other.example/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken);
    }
}
