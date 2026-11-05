// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp;

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
