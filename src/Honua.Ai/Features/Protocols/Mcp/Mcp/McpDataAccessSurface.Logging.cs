// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp;

internal sealed partial class McpDataAccessSurface
{
    // notifications/message travels only on the server-initiated stream, so logging is
    // advertised and accepted only while that stream is enabled.
    private static bool LoggingAvailable(HttpContext httpContext) =>
        httpContext.RequestServices?.GetService<IOptions<McpOptions>>()?.Value.ServerInitiatedStreamEnabled == true;

    private static async Task<McpJsonRpcResponse> SetLogLevelAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var sessions = httpContext.RequestServices?.GetService<McpSessionManager>();
        if (!LoggingAvailable(httpContext) || sessions is null)
        {
            return ErrorResponse(request.Id, McpErrorMapper.MethodNotFound(
                "logging/setLevel is unavailable: this server does not advertise the logging capability."));
        }

        var level = ParseParams(request.Params, McpJsonContext.Default.McpSetLevelParams)?.Level;
        if (!McpLogging.IsKnownLevel(level))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(
                "logging/setLevel requires level: debug, info, notice, warning, error, critical, alert or emergency."));
        }

        var sessionId = httpContext.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        if (!await sessions.SetLogLevelAsync(sessionId, level!, cancellationToken).ConfigureAwait(false))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(
                "logging/setLevel requires an active MCP session (Mcp-Session-Id)."));
        }

        return Ping(request);
    }
}
