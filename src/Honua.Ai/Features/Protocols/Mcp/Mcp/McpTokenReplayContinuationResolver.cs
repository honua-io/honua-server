// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Names the MCP session a request on the <c>/mcp</c> transport continues, so token
/// replay protection admits reuse of a bearer token bound to that session and nowhere
/// else (honua-server#4909). Only the MCP transport routes answer; the same token and
/// session header presented to any other route remain a replay.
/// </summary>
internal sealed class McpTokenReplayContinuationResolver : ITokenReplayContinuationResolver
{
    public string? ResolveContinuationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!McpBearerAuthenticationEndpointExtensions.IsMcpTransportPath(context.Request.Path))
        {
            return null;
        }

        var sessionId = context.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        return McpEndpointExtensions.IsWellFormedSessionId(sessionId)
            ? McpEndpointExtensions.TokenReplayContinuationId(sessionId)
            : null;
    }
}
