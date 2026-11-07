// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Ai.Protocols.Mcp;

internal sealed class McpTokenReplayScopeResolver : ITokenReplayScopeResolver
{
    public bool OwnsRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return McpBearerAuthenticationEndpointExtensions.IsMcpTransportPath(context.Request.Path);
    }

    public TokenReplayScope ResolveScope(HttpContext context, string? validatedTokenFingerprint)
    {
        if (!OwnsRequest(context))
        {
            return TokenReplayScope.HttpApi;
        }

        if (IsSessionlessCandidate(context.Request) && validatedTokenFingerprint is not null)
        {
            return TokenReplayScope.ReusableSurface("mcp", validatedTokenFingerprint);
        }

        var sessionId = context.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        var continuationId = !McpModernProtocol.HasModernHeader(context.Request)
            && McpEndpointExtensions.IsWellFormedSessionId(sessionId)
                ? McpEndpointExtensions.TokenReplayContinuationId(sessionId) : null;
        return TokenReplayScope.ContinuationSurface(continuationId is null
            ? null : BuildTokenReplayContinuationMarker(continuationId));
    }

    private static bool IsSessionlessCandidate(HttpRequest request)
    {
        var versions = request.Headers[McpModernProtocol.VersionHeader];
        if (versions.Count != 1 || versions[0] != McpModernProtocol.Version)
        {
            return false;
        }

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
        {
            return true;
        }

        var methods = request.Headers["Mcp-Method"];
        return methods.Count == 1 && !string.IsNullOrWhiteSpace(methods[0]) && methods[0] != "initialize";
    }
}
