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
    [UnitTheory]
    [InlineData(true, "warning", true, "warning")]
    [InlineData(true, "verbose", true, null)]
    [InlineData(true, "warning", false, null)]
    [InlineData(false, "warning", true, null)]
    public async Task SetLevel_StoresAKnownLevel_OnlyForALiveSessionWithTheStreamOn(
        bool streamEnabled, string level, bool sessionKnown, string? storedLevel)
    {
        await using var sessions = new McpSessionManager();
        var sessionId = sessions.CreateSession();
        var context = CreateContext(sessions, streamEnabled, sessionKnown ? sessionId : "unknown-session");
        var surface = new McpDataAccessSurface([], [], NullLogger<McpDataAccessSurface>.Instance);
        using var parameters = JsonDocument.Parse($$"""{"level":"{{level}}"}""");

        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement(1),
            Method = "logging/setLevel",
            Params = parameters.RootElement.Clone()
        }, CancellationToken.None);

        (response!.Error is null).Should().Be(storedLevel is not null, response.Error?.Message);
        (await sessions.GetLogLevelAsync(sessionId, CancellationToken.None)).Should().Be(storedLevel);
    }

    [UnitTheory]
    [InlineData(null, true)]
    [InlineData("debug", true)]
    [InlineData("info", true)]
    [InlineData("notice", false)]
    [InlineData("emergency", false)]
    public void Info_IsDeliveredUnlessTheSessionAskedForAHigherLevel(string? sessionLevel, bool delivered) =>
        McpLogging.IsEnabled(sessionLevel, McpLogging.Info).Should().Be(delivered);

    private static DefaultHttpContext CreateContext(McpSessionManager sessions, bool streamEnabled, string sessionId)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton(Options.Create(new McpOptions { ServerInitiatedStreamEnabled = streamEnabled }))
            .BuildServiceProvider();
        context.Request.Headers[McpSessionManager.SessionHeaderName] = sessionId;
        return context;
    }
}
