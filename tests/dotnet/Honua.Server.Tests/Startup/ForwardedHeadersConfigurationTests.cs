// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Server.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

public sealed class ForwardedHeadersConfigurationTests
{
    private const string LoadBalancerAddress = "10.20.3.17";
    private const string ViewerAddress = "198.51.100.7";
    private const string VpcOriginInterfaceAddress = "10.20.9.4";

    private static readonly Dictionary<string, string?> CloudFrontBehindLoadBalancer = new()
    {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:ForwardLimit"] = "2",
        ["ForwardedHeaders:ProtoHeaderName"] = "CloudFront-Forwarded-Proto",
        ["ForwardedHeaders:KnownNetworks:0"] = "10.20.0.0/16",
    };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_PeerInsideKnownNetworkWithConfiguredProtoHeader_RequestIsHttps()
    {
        var seen = await SendThroughLoadBalancerAsync(CloudFrontBehindLoadBalancer, LoadBalancerAddress);

        seen.Scheme.Should().Be("https");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_TwoTrustedHops_ResolvesTheViewerAddress()
    {
        var seen = await SendThroughLoadBalancerAsync(CloudFrontBehindLoadBalancer, LoadBalancerAddress);

        seen.RemoteAddress.Should().Be(ViewerAddress);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_ViewerSuppliedForwardedFor_IsNotTrusted()
    {
        var seen = await SendThroughLoadBalancerAsync(
            CloudFrontBehindLoadBalancer, LoadBalancerAddress, viewerSuppliedForwardedFor: "192.0.2.200");

        seen.RemoteAddress.Should().Be(ViewerAddress);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_PeerOutsideKnownNetworks_IgnoresForwardedScheme()
    {
        var seen = await SendThroughLoadBalancerAsync(CloudFrontBehindLoadBalancer, "192.0.2.44");

        seen.Scheme.Should().Be("http");
        seen.RemoteAddress.Should().Be("192.0.2.44");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_DefaultProtoHeader_KeepsLoadBalancerHopScheme()
    {
        var settings = new Dictionary<string, string?>(CloudFrontBehindLoadBalancer);
        settings.Remove("ForwardedHeaders:ProtoHeaderName");

        var seen = await SendThroughLoadBalancerAsync(settings, LoadBalancerAddress);

        seen.Scheme.Should().Be("http");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void ConfigureForwardedHeaders_MalformedKnownNetwork_FailsNamingTheEntry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(CloudFrontBehindLoadBalancer)
            {
                ["ForwardedHeaders:KnownNetworks:0"] = "10.20.0.0/40",
            })
            .Build();

        var configure = () => StartupConfigurationHelpers.ConfigureForwardedHeaders(new ServiceCollection(), configuration);

        configure.Should().Throw<InvalidOperationException>()
            .WithMessage("*ForwardedHeaders:KnownNetworks*10.20.0.0/40*");
    }

    private sealed record ServerView(string Scheme, string RemoteAddress);

    private static async Task<ServerView> SendThroughLoadBalancerAsync(
        Dictionary<string, string?> settings,
        string peerAddress,
        string? viewerSuppliedForwardedFor = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        StartupConfigurationHelpers.ConfigureForwardedHeaders(builder.Services, builder.Configuration).Should().BeTrue();

        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peerAddress);
            return next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/seen", (HttpContext context) => $"{context.Request.Scheme} {context.Connection.RemoteIpAddress}");
        await app.StartAsync();

        var forwardedFor = viewerSuppliedForwardedFor is null
            ? $"{ViewerAddress}, {VpcOriginInterfaceAddress}"
            : $"{viewerSuppliedForwardedFor}, {ViewerAddress}, {VpcOriginInterfaceAddress}";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/seen");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Add("CloudFront-Forwarded-Proto", "https");
        using var response = await app.GetTestClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        var parts = (await response.Content.ReadAsStringAsync()).Split(' ');
        return new ServerView(parts[0], parts[1]);
    }
}
