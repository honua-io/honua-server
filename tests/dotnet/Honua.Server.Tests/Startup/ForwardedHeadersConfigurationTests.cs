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

    private static readonly Dictionary<string, string?> CloudFrontBehindLoadBalancer = new()
    {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:ProtoHeaderName"] = "CloudFront-Forwarded-Proto",
        ["ForwardedHeaders:KnownNetworks:0"] = "10.20.0.0/16",
    };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_PeerInsideKnownNetworkWithConfiguredProtoHeader_RequestIsHttps()
    {
        var isHttps = await SendThroughLoadBalancerAsync(CloudFrontBehindLoadBalancer, LoadBalancerAddress);

        isHttps.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_PeerOutsideKnownNetworks_IgnoresForwardedScheme()
    {
        var isHttps = await SendThroughLoadBalancerAsync(CloudFrontBehindLoadBalancer, "192.0.2.44");

        isHttps.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ConfigureForwardedHeaders_DefaultProtoHeader_KeepsLoadBalancerHopScheme()
    {
        var settings = new Dictionary<string, string?>(CloudFrontBehindLoadBalancer);
        settings.Remove("ForwardedHeaders:ProtoHeaderName");

        var isHttps = await SendThroughLoadBalancerAsync(settings, LoadBalancerAddress);

        isHttps.Should().BeFalse();
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

    private static async Task<bool> SendThroughLoadBalancerAsync(
        Dictionary<string, string?> settings,
        string peerAddress)
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
        app.MapGet("/scheme", (HttpContext context) => context.Request.IsHttps ? "https" : "http");
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/scheme");
        request.Headers.Add("X-Forwarded-For", "198.51.100.7, 203.0.113.9");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Add("CloudFront-Forwarded-Proto", "https");
        using var response = await app.GetTestClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync() == "https";
    }
}
