// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpSkillsTests
{
    private readonly McpDataAccessSurface _surface = new([], [],
        NullLogger<McpDataAccessSurface>.Instance, limits: new McpSurfaceLimits(2, 20));

    [UnitTest]
    public async Task List_PagesAllSkillsWithoutDuplicates_AndRejectsMalformedCursor()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var response = await DispatchAsync("skills/list", JsonSerializer.Serialize(new { cursor }));
            Assert.Null(response.Error);
            var result = response.Result!.Value;
            foreach (var skill in result.GetProperty("skills").EnumerateArray())
                Assert.True(names.Add(skill.GetProperty("uri").GetString()!));
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        } while (cursor is not null);
        Assert.Equal(5, names.Count);
        var invalid = await DispatchAsync("skills/list", "{\"cursor\":\"garbage\"}");
        Assert.Equal(-32602, invalid.Error!.Code);
    }

    [UnitTest]
    public async Task LegacyRequest_DoesNotExposeSkillsMethods()
    {
        var response = await DispatchAsync("skills/list", "{}", new DefaultHttpContext());
        Assert.Equal(-32601, response.Error!.Code);
    }

    [UnitTest]
    public async Task Read_RequiresAuthentication_AndRejectsPartialSkillFiles()
    {
        const string parameters = "{\"uri\":\"skill://honua-install/SKILL.md\"}";
        var anonymous = await DispatchAsync("resources/read", parameters, new DefaultHttpContext());
        Assert.NotNull(anonymous.Error);
        var whole = await DispatchAsync("resources/read", parameters);
        Assert.Null(whole.Error);
        var result = whole.Result!.Value;
        Assert.False(result.TryGetProperty("nextCursor", out _));
        Assert.True(result.GetProperty("contents")[0].GetProperty("text").GetString()!.Length > 20);
        var partial = await DispatchAsync("resources/read",
            "{\"uri\":\"skill://honua-install/SKILL.md\",\"maxChars\":20}");
        Assert.Equal(-32602, partial.Error!.Code);
    }

    private async Task<McpJsonRpcResponse> DispatchAsync(string method, string parameters, HttpContext? context = null)
    {
        if (context is null)
        {
            context = McpTestFactory.AuthenticatedHttpContext();
            context.Items[McpModernProtocol.RequestItemKey] = true;
        }
        var request = JsonSerializer.Deserialize(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":" + JsonSerializer.Serialize(method) + ",\"params\":" + parameters + "}",
            McpJsonContext.Default.McpJsonRpcRequest)!;
        return (await _surface.DispatchAsync(context, request, CancellationToken.None))!;
    }
}
