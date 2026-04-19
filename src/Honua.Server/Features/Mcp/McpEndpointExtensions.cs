// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Maps the single JSON-RPC endpoint that hosts the MCP operator surface.
/// Clients POST one MCP request per HTTP request; batch framing is deferred.
/// </summary>
internal static class McpEndpointExtensions
{
    /// <summary>
    /// Route for the MCP operator surface.
    /// </summary>
    public const string RoutePath = "/mcp";

    private const string JsonMimeType = "application/json";

    /// <summary>
    /// Maps <c>POST /mcp</c> for JSON-RPC dispatch.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpOperatorSurface(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(RoutePath,
                static (HttpContext context, CancellationToken ct) => HandleAsync(context, ct))
            .WithDisplayName("MCP Operator Surface")
            .WithName("McpOperatorSurface")
            .WithSummary("MCP JSON-RPC dispatcher for planning, execution, lifecycle, and results.")
            .WithDescription("Accepts JSON-RPC 2.0 requests for initialize, tools/list, tools/call, resources/list, and resources/read.")
            .WithTags("Mcp");

        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var surface = context.RequestServices.GetRequiredService<McpOperatorSurface>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpOperatorSurface>>();

        McpJsonRpcRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync(context.Request.Body, McpJsonContext.Default.McpJsonRpcRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            McpLog.RequestParseFailed(logger, ex.Message);
            await WriteResponseAsync(
                context,
                new McpJsonRpcResponse
                {
                    Error = McpErrorMapper.InvalidArgument($"Request body is not valid JSON-RPC: {ex.Message}")
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteResponseAsync(
                context,
                new McpJsonRpcResponse
                {
                    Error = McpErrorMapper.InvalidArgument("Request body is required.")
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = await surface
            .DispatchAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        await WriteResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
        HttpContext context,
        McpJsonRpcResponse response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonMimeType;
        await JsonSerializer
            .SerializeAsync(context.Response.Body, response, McpJsonContext.Default.McpJsonRpcResponse, cancellationToken)
            .ConfigureAwait(false);
    }
}
