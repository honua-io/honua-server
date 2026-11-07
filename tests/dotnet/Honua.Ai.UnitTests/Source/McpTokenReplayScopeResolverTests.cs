// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class McpTokenReplayScopeResolverTests
{
    [UnitTheory]
    [InlineData("/mcp", true)]
    [InlineData("/mcp/", true)]
    [InlineData("/MCP", true)]
    [InlineData("/mcp/other", false)]
    [InlineData("/mcp-other", false)]
    [InlineData("/api/v1/studio/package-families", false)]
    [InlineData("/.well-known/oauth-protected-resource/mcp", false)]
    public void Resolve_ModernHeaders_ClaimOnlyExactTransportPaths(string path, bool owned)
    {
        var context = ModernContext();
        context.Request.Path = path;
        var resolver = new McpTokenReplayScopeResolver();
        Assert.Equal(owned, resolver.OwnsRequest(context));
        Assert.Equal(owned ? TokenReplayScope.ReusableSurface("mcp", "validated-token") : TokenReplayScope.HttpApi,
            resolver.ResolveScope(context, "validated-token"));
    }

    [UnitTheory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public void Resolve_ModernSessionVerbs_StayInSessionlessScope(string method)
    {
        var context = ModernContext();
        context.Request.Method = method;
        context.Request.Headers.Remove("Mcp-Method");
        Assert.Equal(TokenReplayScope.ReusableSurface("mcp", "validated-token"),
            new McpTokenReplayScopeResolver().ResolveScope(context, "validated-token"));
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("initialize")]
    public void Resolve_AbsentOrInitializeMethod_RetainsLegacyAdmission(string? method)
    {
        var context = ModernContext();
        context.Request.Headers.Remove("Mcp-Method");
        if (method is not null)
        {
            context.Request.Headers["Mcp-Method"] = method;
        }
        Assert.Equal(TokenReplayScope.ContinuationSurface(null),
            new McpTokenReplayScopeResolver().ResolveScope(context, "validated-token"));
    }

    [UnitTheory]
    [InlineData("version")]
    [InlineData("method")]
    [InlineData("unsupported-version")]
    [InlineData("no-fingerprint")]
    public void Resolve_AmbiguousOrUnvalidatedRequest_NeverCreatesReusableScope(string invalidity)
    {
        var context = ModernContext();
        if (invalidity == "version")
        {
            context.Request.Headers["MCP-Protocol-Version"] = new StringValues(["2026-07-28", "2026-07-28"]);
        }
        if (invalidity == "method")
        {
            context.Request.Headers["Mcp-Method"] = new StringValues(["server/discover", "initialize"]);
        }
        if (invalidity == "unsupported-version")
        {
            context.Request.Headers["MCP-Protocol-Version"] = "2099-01-01";
        }
        Assert.True(new McpTokenReplayScopeResolver().ResolveScope(context,
            invalidity == "no-fingerprint" ? null : "validated-token").CanBindContinuation);
    }

    [UnitTest]
    public void Resolve_LegacySession_PreservesContinuationMarker()
    {
        var context = ModernContext();
        context.Request.Headers.Remove("MCP-Protocol-Version");
        context.Request.Headers["Mcp-Session-Id"] = "session-a";
        var continuation = McpEndpointExtensions.TokenReplayContinuationId("session-a");
        Assert.Equal(TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker(continuation)),
            new McpTokenReplayScopeResolver().ResolveScope(context, "validated-token"));
    }

    private static DefaultHttpContext ModernContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = "POST";
        context.Request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        context.Request.Headers["Mcp-Method"] = "server/discover";
        return context;
    }
}
