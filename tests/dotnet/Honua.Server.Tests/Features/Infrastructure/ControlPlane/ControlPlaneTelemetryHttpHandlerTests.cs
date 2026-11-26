// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ControlPlaneTelemetryHttpHandlerTests
{
    [UnitTest]
    public void CreateTelemetryHttpHandler_DisablesRedirectsProxyAndCookies()
    {
        using var handler = ControlPlaneHttpClients.CreateTelemetryHttpHandler(() => new ControlPlaneOptions());

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
    }

    [UnitTest]
    public async Task CreateTelemetryHttpHandler_BlocksPrivateNetworkDestinationByDefault()
    {
        using var handler = ControlPlaneHttpClients.CreateTelemetryHttpHandler(
            () => new ControlPlaneOptions(),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }));

        using var client = new HttpClient(handler);
        var act = async () => await client.GetAsync("https://metrics.example.test/api/v1/query");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [UnitTest]
    public async Task CreateTelemetryHttpHandler_RejectsHttpWhenPrivateNetworksNotAllowedForTarget()
    {
        var options = new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "other-prom",
                    BaseUrl = "http://10.0.0.5:9090",
                    AllowPrivateNetworks = true
                }
            ]
        };

        using var handler = ControlPlaneHttpClients.CreateTelemetryHttpHandler(
            () => options,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.99") }));

        using var client = new HttpClient(handler);
        var act = async () => await client.GetAsync("http://10.0.0.99:9090/api/v1/query");

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
