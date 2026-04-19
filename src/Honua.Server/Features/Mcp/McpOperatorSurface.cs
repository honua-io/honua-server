// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;
using Honua.Server.Features.Mcp.Resources;
using Honua.Server.Features.Mcp.Tools;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Central JSON-RPC dispatcher and registry for the MCP operator surface.
/// Hosts the tool and resource catalogs, routes <c>initialize</c>,
/// <c>tools/list</c>, <c>tools/call</c>, <c>resources/list</c>, and
/// <c>resources/read</c> methods, and converts domain exceptions into JSON-RPC
/// errors via <see cref="McpErrorMapper"/>.
/// </summary>
internal sealed class McpOperatorSurface
{
    private readonly IReadOnlyDictionary<string, IMcpTool> _tools;
    private readonly IReadOnlyList<IMcpResource> _resources;
    private readonly ILogger<McpOperatorSurface> _logger;

    public McpOperatorSurface(
        IEnumerable<IMcpTool> tools,
        IEnumerable<IMcpResource> resources,
        ILogger<McpOperatorSurface> logger)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _resources = resources.ToList();
        _logger = logger;

        McpLog.SurfaceInitialized(_logger, _tools.Count, _resources.Sum(r => r.Describe().Count));
    }

    public IReadOnlyCollection<string> ToolNames => (IReadOnlyCollection<string>)_tools.Keys;

    public IReadOnlyList<IMcpResource> Resources => _resources;

    /// <summary>
    /// Dispatches a JSON-RPC request and returns a response envelope. Uses
    /// <see cref="McpErrorMapper"/> to translate domain exceptions into JSON-RPC
    /// error objects; other unhandled exceptions surface as <c>internal</c>.
    /// </summary>
    public async Task<McpJsonRpcResponse> DispatchAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("jsonrpc must be \"2.0\"."));
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("method is required."));
        }

        try
        {
            return request.Method switch
            {
                "initialize" => SuccessResponse(request.Id, Initialize(), McpJsonContext.Default.McpInitializeResult),
                "tools/list" => SuccessResponse(request.Id, ListTools(), McpJsonContext.Default.McpToolsListResult),
                "tools/call" => await CallToolAsync(httpContext, request, cancellationToken).ConfigureAwait(false),
                "resources/list" => SuccessResponse(request.Id, ListResources(), McpJsonContext.Default.McpResourcesListResult),
                "resources/read" => await ReadResourceAsync(httpContext, request, cancellationToken).ConfigureAwait(false),
                _ => ErrorResponse(request.Id, McpErrorMapper.NotFound($"Unknown MCP method '{request.Method}'."))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorResponse(request.Id, McpErrorMapper.Map(ex));
        }
    }

    private static McpInitializeResult Initialize() => new();

    private McpToolsListResult ListTools() => new()
    {
        Tools = _tools.Values
            .Select(t => t.Describe())
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList()
    };

    private async Task<McpJsonRpcResponse> CallToolAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParams(request.Params, McpJsonContext.Default.McpToolsCallParams);
        if (string.IsNullOrWhiteSpace(parameters?.Name))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("tools/call requires a tool name."));
        }

        if (!_tools.TryGetValue(parameters.Name, out var tool))
        {
            return ErrorResponse(request.Id, McpErrorMapper.NotFound($"Unknown MCP tool '{parameters.Name}'."));
        }

        try
        {
            var result = await tool
                .InvokeAsync(httpContext, parameters.Arguments, cancellationToken)
                .ConfigureAwait(false);
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", tool.Name),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Ok),
                new KeyValuePair<string, object?>("workflow_family", tool.WorkflowFamily));
            McpLog.ToolCompleted(_logger, tool.Name, McpTelemetry.Status.Ok);
            return SuccessResponse(request.Id, result, McpJsonContext.Default.McpToolsCallResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = McpErrorMapper.Map(ex);
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", tool.Name),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error),
                new KeyValuePair<string, object?>("workflow_family", tool.WorkflowFamily));
            McpLog.ToolFailed(_logger, tool.Name, error.Data?.Code ?? McpErrorMapper.Codes.Internal, error.Message);
            return ErrorResponse(request.Id, error);
        }
    }

    private McpResourcesListResult ListResources() => new()
    {
        Resources = _resources
            .SelectMany(r => r.Describe())
            .OrderBy(d => d.Uri, StringComparer.Ordinal)
            .ToList()
    };

    private async Task<McpJsonRpcResponse> ReadResourceAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParams(request.Params, McpJsonContext.Default.McpResourcesReadParams);
        if (string.IsNullOrWhiteSpace(parameters?.Uri))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("resources/read requires a URI."));
        }

        var handler = _resources.FirstOrDefault(r => r.CanHandle(parameters.Uri));
        if (handler is null)
        {
            return ErrorResponse(request.Id, McpErrorMapper.NotFound($"Unknown MCP resource '{parameters.Uri}'."));
        }

        try
        {
            var result = await handler
                .ReadAsync(httpContext, parameters.Uri, cancellationToken)
                .ConfigureAwait(false);
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", handler.Family),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Ok));
            return SuccessResponse(request.Id, result, McpJsonContext.Default.McpResourcesReadResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = McpErrorMapper.Map(ex);
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", handler.Family),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error));
            return ErrorResponse(request.Id, error);
        }
    }

    private static T? ParseParams<T>(JsonElement? parameters, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return parameters.Value.Deserialize(typeInfo);
        }
        catch (JsonException ex)
        {
            throw new GeoprocessingValidationException(
                $"JSON-RPC params are not valid: {ex.Message}");
        }
    }

    private static McpJsonRpcResponse SuccessResponse<T>(
        JsonElement? id,
        T payload,
        JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        using var document = JsonDocument.Parse(json);
        return new McpJsonRpcResponse
        {
            Id = id,
            Result = document.RootElement.Clone()
        };
    }

    private static McpJsonRpcResponse ErrorResponse(JsonElement? id, McpJsonRpcError error) => new()
    {
        Id = id,
        Error = error
    };
}
