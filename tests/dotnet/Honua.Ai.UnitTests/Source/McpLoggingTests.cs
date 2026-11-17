// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpLoggingTests
{
    private static readonly McpOptions StreamOn = new() { ServerInitiatedStreamEnabled = true };

    [UnitTest]
    public async Task SetLevel_KnownLevelOnALiveSession_IsStoredForTheSession()
    {
        await using var sessions = new McpSessionManager();
        var sessionId = sessions.CreateSession();

        var response = await SetLevelAsync(CreateContext(sessions, StreamOn, sessionId), "warning");

        response.Error.Should().BeNull();
        (await sessions.GetLogLevelAsync(sessionId, CancellationToken.None)).Should().Be("warning");
    }

    [UnitTheory]
    [InlineData("verbose")]
    [InlineData("INFO")]
    public async Task SetLevel_UnknownLevel_IsRefusedAndNothingIsStored(string level)
    {
        await using var sessions = new McpSessionManager();
        var sessionId = sessions.CreateSession();

        var response = await SetLevelAsync(CreateContext(sessions, StreamOn, sessionId), level);

        response.Error.Should().NotBeNull();
        (await sessions.GetLogLevelAsync(sessionId, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task SetLevel_ForASessionThisServerDoesNotKnow_IsRefused()
    {
        await using var sessions = new McpSessionManager();

        var response = await SetLevelAsync(CreateContext(sessions, StreamOn, "unknown-session"), "warning");

        response.Error!.Message.Should().Contain("active MCP session");
    }

    [UnitTest]
    public async Task SetLevel_WhileTheServerStreamIsOff_IsNotOffered()
    {
        await using var sessions = new McpSessionManager();
        var sessionId = sessions.CreateSession();

        var response = await SetLevelAsync(CreateContext(sessions, new McpOptions(), sessionId), "warning");

        response.Error!.Message.Should().Contain("does not advertise the logging capability");
        (await sessions.GetLogLevelAsync(sessionId, CancellationToken.None)).Should().BeNull();
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("debug")]
    [InlineData("info")]
    public void Info_IsDeliveredAtOrBelowTheSessionLevel(string? sessionLevel) =>
        McpLogging.IsEnabled(sessionLevel, McpLogging.Info).Should().BeTrue();

    [UnitTheory]
    [InlineData("notice")]
    [InlineData("emergency")]
    public void Info_IsSuppressedAboveTheSessionLevel(string sessionLevel) =>
        McpLogging.IsEnabled(sessionLevel, McpLogging.Info).Should().BeFalse();

    private static async Task<McpJsonRpcResponse> SetLevelAsync(HttpContext context, string level)
    {
        using var parameters = JsonDocument.Parse($$"""{"level":"{{level}}"}""");
        var surface = new McpDataAccessSurface([], [], NullLogger<McpDataAccessSurface>.Instance);
        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement(1),
            Method = "logging/setLevel",
            Params = parameters.RootElement.Clone()
        }, CancellationToken.None);
        return response!;
    }

    private static DefaultHttpContext CreateContext(McpSessionManager sessions, McpOptions options, string sessionId)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton(Options.Create(options))
            .BuildServiceProvider();
        context.Request.Headers[McpSessionManager.SessionHeaderName] = sessionId;
        return context;
    }
}
