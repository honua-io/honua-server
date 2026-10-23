// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Claims the <c>/mcp</c> transport as a token-replay continuation surface and names the MCP
/// session a request continues, so token replay protection admits reuse of a bearer token
/// bound to that session and nowhere else (honua-server#4909). Only the MCP transport routes
/// are claimed: a token bound to a session is a replay on any other route, and a token
/// admitted on the ordinary HTTP API is a replay here (honua-server#4899).
/// </summary>
internal sealed class McpTokenReplayContinuationResolver : ITokenReplayContinuationResolver
{
    public bool OwnsRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return McpBearerAuthenticationEndpointExtensions.IsMcpTransportPath(context.Request.Path);
    }

    public string? ResolveContinuationId(HttpContext context)
    {
        if (!OwnsRequest(context))
        {
            return null;
        }

        var sessionId = context.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        return McpEndpointExtensions.IsWellFormedSessionId(sessionId)
            ? McpEndpointExtensions.TokenReplayContinuationId(sessionId)
            : null;
    }
}
