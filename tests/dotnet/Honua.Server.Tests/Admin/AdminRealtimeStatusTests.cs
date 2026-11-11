// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Honua.Server.Tests.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Streaming)]
public sealed class AdminRealtimeStatusTests
{
    private const string AdminPassword = "admin-realtime-status-password";

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task NonAdminConnection_CannotConnect()
    {
        using var factory = CreateFactory();
        await using var connection = BuildConnection(factory, apiKey: null);

        var connect = async () => await connection.StartAsync();

        await connect.Should().ThrowAsync<Exception>();
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task GetStatus_DescribesThisNodeWithoutCrossNodeGroups()
    {
        using var factory = CreateFactory();
        await using var connection = BuildConnection(factory, AdminPassword);
        await connection.StartAsync();

        var status = await connection.InvokeAsync<JsonElement>("GetStatus");

        status.TryGetProperty("instanceId", out _).Should().BeTrue();
        status.TryGetProperty("backplaneEnabled", out _).Should().BeFalse();
        status.TryGetProperty("realtimeGroups", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [Endpoint("POST /hubs/admin/negotiate")]
    [InlineData("SubscribeToOpsHealth")]
    [InlineData("SubscribeToOperateEvents")]
    [InlineData("SubscribeToDeployOperations")]
    public async Task SubscribeToOperationalGroup_IsRefused_SoTheConsolePolls(string method)
    {
        using var factory = CreateFactory();
        await using var connection = BuildConnection(factory, AdminPassword);
        await connection.StartAsync();

        var subscribe = async () => await connection.InvokeAsync(method);

        await subscribe.Should().ThrowAsync<HubException>();
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task SubscribeToProposals_IsAccepted()
    {
        using var factory = CreateFactory();
        await using var connection = BuildConnection(factory, AdminPassword);
        await connection.StartAsync();

        var subscribe = async () => await connection.InvokeAsync("SubscribeToProposals");

        await subscribe.Should().NotThrowAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory()
        => new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                    });
                });
            });

    private static HubConnection BuildConnection(WebApplicationFactory<Program> factory, string? apiKey)
    {
        var server = factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, AdminRealtimeContract.HubPath.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (apiKey is not null)
                {
                    options.Headers["X-API-Key"] = apiKey;
                }
            })
            .Build();
    }
}
