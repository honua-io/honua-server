// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Prompts;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Central JSON-RPC dispatcher and registry for the open MCP data-access
/// surface — the studio/data-access tools plus the bounded, read-only
/// ops-evidence tools this repo serves publicly. This is the evidence side of
/// the evidence-vs-intelligence boundary (ADR-0066); operator <em>intelligence</em>
/// (diagnose/tune/upgrade-planning with rollback gates) lives in the private
/// <c>honua-devops</c> operator surface, not here.
/// Hosts the tool and resource catalogs, routes <c>initialize</c>,
/// <c>notifications/initialized</c>, <c>tools/list</c>, <c>tools/call</c>,
/// <c>resources/list</c>, <c>resources/templates/list</c>,
/// <c>resources/read</c>, <c>prompts/list</c>, and <c>prompts/get</c> methods,
/// and converts domain exceptions into JSON-RPC errors via
/// <see cref="McpErrorMapper"/>.
/// </summary>
internal sealed class McpDataAccessSurface
{
    /// <summary>
    /// MCP protocol revisions this server can negotiate during <c>initialize</c>.
    /// Listed newest-first so the latest entry is returned when the client asks
    /// for an unsupported revision. <c>2025-06-18</c> is the default negotiated
    /// revision (honua-server#1954); it adds the native tool <c>outputSchema</c>
    /// field this server publishes. <c>2025-03-26</c> remains supported for older
    /// clients, which negotiate it explicitly.
    /// See https://modelcontextprotocol.io/docs/learn/versioning.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ["2025-06-18", "2025-03-26"];

    /// <summary>
    /// Latest MCP protocol revision supported by this server. Used when the
    /// client requests a version we do not implement.
    /// </summary>
    public static string LatestProtocolVersion => SupportedProtocolVersions[0];

    private readonly IReadOnlyDictionary<string, IMcpTool> _tools;
    private readonly List<IMcpToolSource> _toolSources;
    private readonly IReadOnlyList<IMcpResource> _resources;
    private readonly McpSurfaceLimits _limits;
    private readonly ILogger<McpDataAccessSurface> _logger;

    public McpDataAccessSurface(
        IEnumerable<IMcpTool> tools,
        IEnumerable<IMcpResource> resources,
        ILogger<McpDataAccessSurface> logger,
        McpSurfaceLimits? limits = null,
        IEnumerable<IMcpToolSource>? toolSources = null)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        // Dynamic, runtime-published tools (#2483). None composed by default, so the
        // surface behaves exactly as before unless a host opts a source in.
        _toolSources = toolSources?.ToList() ?? [];
        _resources = resources.ToList();
        _limits = limits ?? McpSurfaceLimits.Default;
        _logger = logger;

        McpLog.SurfaceInitialized(
            _logger,
            _tools.Count,
            _resources.Sum(r => r.Describe().Count + r.DescribeTemplates().Count));
    }

    public IReadOnlyCollection<string> ToolNames => (IReadOnlyCollection<string>)_tools.Keys;

    /// <summary>
    /// The registered tool handlers, exposed so the <c>honua_list_capabilities</c>
    /// tool (#1949) can project the live tool catalog into a self-describing
    /// manifest. This is the same single source-of-truth catalog served over both
    /// the HTTP-SSE and stdio transports (#1950), so the manifest is
    /// transport-symmetric.
    /// </summary>
    public IReadOnlyCollection<IMcpTool> ToolHandlers => (IReadOnlyCollection<IMcpTool>)_tools.Values;

    public IReadOnlyList<IMcpResource> Resources => _resources;

    /// <summary>
    /// Dispatches a JSON-RPC request and returns a response envelope, or
    /// <c>null</c> when the input is a valid MCP notification — meaning a
    /// <c>notifications/*</c> method with no <c>id</c>. Per MCP 2025-03-26 the
    /// <c>notifications/*</c> prefix is the only way a message carries
    /// notification semantics: a non-<c>notifications/*</c> method without an
    /// <c>id</c>, or a <c>notifications/*</c> message that carries an
    /// <c>id</c>, is malformed and surfaced as <c>invalid_request</c>
    /// (<c>id: null</c> when the server cannot echo one) so clients see the
    /// error instead of an HTTP 202 that looks like success. Callers are
    /// expected to validate the shape of the request id (string or integer)
    /// before calling this method; invalid ids must be surfaced as JSON-RPC
    /// <c>invalid_request</c> errors by the transport layer.
    /// </summary>
    public async Task<McpJsonRpcResponse?> DispatchAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var isNotification = request.Id is null;

        if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
        {
            return ErrorResponse(
                isNotification ? McpEndpointExtensions.JsonNullId : request.Id,
                McpErrorMapper.InvalidRequest("jsonrpc must be \"2.0\"."));
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return ErrorResponse(
                isNotification ? McpEndpointExtensions.JsonNullId : request.Id,
                McpErrorMapper.InvalidRequest("method is required."));
        }

        // MCP lifecycle notifications carry no response per JSON-RPC 2.0.
        // `notifications/initialized` is required after a successful initialize;
        // unknown notifications are dropped silently rather than erroring so
        // forward-compatible clients can layer in new notification types. A
        // `notifications/*` message that carries an id violates the JSON-RPC
        // 2.0 / MCP schema (notifications MUST NOT include an id), so it must
        // be surfaced as invalid_request rather than silently accepted.
        if (request.Method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return isNotification
                ? null
                : ErrorResponse(
                    request.Id,
                    McpErrorMapper.InvalidRequest(
                        "notifications/* messages MUST NOT include an id per JSON-RPC 2.0 and MCP 2025-03-26."));
        }

        if (isNotification)
        {
            // Only `notifications/*` methods may omit id in MCP 2025-03-26.
            // A non-notifications method without an id is a malformed request,
            // not a valid notification; surfacing invalid_request with id: null
            // (JSON-RPC 2.0's fallback for unknown ids) helps client developers
            // catch their own bugs — for example an `initialize` call with a
            // missing id — instead of seeing an HTTP 202 that looks like
            // success.
            return ErrorResponse(
                McpEndpointExtensions.JsonNullId,
                McpErrorMapper.InvalidRequest(
                    $"Method '{request.Method}' requires an id; only notifications/* methods may omit it."));
        }

        // Tag the ambient activity with the MCP protocol and JSON-RPC method as
        // early as possible so every dispatched method — including the handler-
        // less ones (initialize, tools/list, resources/list,
        // resources/templates/list) and the anonymous auth short-circuits in
        // CallToolAsync / ReadResourceAsync — shows up alongside gRPC and
        // GPServer traffic. Concrete tool and resource handlers override the
        // operation tag with their operation name (e.g. "ExecutePlan",
        // "GetJob") further down the call stack.
        McpTelemetry.EnrichActivity(request.Method);

        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(httpContext, request),
                "tools/list" => await ListToolsAsync(request, cancellationToken).ConfigureAwait(false),
                "tools/call" => await CallToolAsync(httpContext, request, cancellationToken).ConfigureAwait(false),
                "resources/list" => ListResources(request),
                "resources/templates/list" => ListResourceTemplates(request),
                "resources/read" => await ReadResourceAsync(httpContext, request, cancellationToken).ConfigureAwait(false),
                "prompts/list" => ListPrompts(request),
                "prompts/get" => GetPrompt(request),
                _ => ErrorResponse(request.Id, McpErrorMapper.MethodNotFound($"Unknown MCP method '{request.Method}'."))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            McpLog.DispatchFailed(_logger, request.Method, ex);
            return ErrorResponse(request.Id, McpErrorMapper.Map(ex));
        }
    }

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which <c>initialize</c> records
    /// whether the client advertised the MCP elicitation capability, so the
    /// transport can bind the flag to the session it issues on success
    /// (honua-server#2484). Request-scoped: initialize and the session-creating
    /// code run within the same <see cref="HttpContext"/>.
    /// </summary>
    internal const string ElicitationCapabilityItemKey = "honua.mcp.client.elicitation";

    private static McpJsonRpcResponse HandleInitialize(HttpContext httpContext, McpJsonRpcRequest request)
    {
        var parameters = ParseParams(request.Params, McpJsonContext.Default.McpInitializeParams);
        if (parameters is null)
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument(
                    "initialize requires params with protocolVersion, capabilities, and clientInfo."));
        }

        if (string.IsNullOrWhiteSpace(parameters.ProtocolVersion))
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument("initialize.params.protocolVersion is required."));
        }

        if (parameters.Capabilities is null
            || parameters.Capabilities.Value.ValueKind != JsonValueKind.Object)
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument("initialize.params.capabilities must be an object."));
        }

        if (parameters.ClientInfo is null || string.IsNullOrWhiteSpace(parameters.ClientInfo.Name))
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument("initialize.params.clientInfo.name is required."));
        }

        // honua-server#2484: record whether the client advertised the elicitation
        // capability so the session issued on success carries it. Per MCP
        // 2025-06-18 an elicitation-capable client sends `capabilities.elicitation`
        // as an object; presence of the key (as an object) is the signal.
        httpContext.Items[ElicitationCapabilityItemKey] =
            AdvertisesElicitation(parameters.Capabilities.Value);

        var negotiatedVersion = NegotiateProtocolVersion(parameters.ProtocolVersion);
        var result = new McpInitializeResult
        {
            ProtocolVersion = negotiatedVersion,
            // honua-server#1954: the server now emits notifications/tools/list_changed
            // and notifications/resources/list_changed over the session SSE stream
            // (e.g. when a publish mutates the catalog), so advertise the listChanged
            // capability instead of leaving the flags inert. The prompt catalog stays
            // static, so prompts.listChanged remains false.
            Capabilities = new McpServerCapabilities
            {
                Tools = new McpCapabilityFlag { ListChanged = true },
                Resources = new McpCapabilityFlag { ListChanged = true },
                Prompts = new McpCapabilityFlag { ListChanged = false }
            }
        };
        return SuccessResponse(request.Id, result, McpJsonContext.Default.McpInitializeResult);
    }

    /// <summary>
    /// Returns <c>true</c> when the client's <c>initialize</c> capabilities object
    /// advertises the MCP elicitation capability (honua-server#2484). Per MCP
    /// 2025-06-18 a supporting client declares <c>"elicitation": {}</c>; presence
    /// of the key as an object is the contract. A non-object <c>elicitation</c>
    /// value is treated as not advertised.
    /// </summary>
    private static bool AdvertisesElicitation(JsonElement capabilities) =>
        capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty("elicitation", out var elicitation)
        && elicitation.ValueKind == JsonValueKind.Object;

    private static string NegotiateProtocolVersion(string requestedVersion)
    {
        for (var i = 0; i < SupportedProtocolVersions.Count; i++)
        {
            if (string.Equals(SupportedProtocolVersions[i], requestedVersion, StringComparison.Ordinal))
            {
                return requestedVersion;
            }
        }

        return LatestProtocolVersion;
    }

    private async Task<McpJsonRpcResponse> ListToolsAsync(
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadCursor(request, out var cursor, out var error))
        {
            return error;
        }

        // Merge the static, registry-bound catalog with any runtime-published tools
        // (#2483). Static tools always win on a name collision, and the dynamic set is
        // empty unless a host composed a tool source, so default hosts are unchanged.
        var describes = _tools.Values.Select(t => t.Describe());
        var dynamicTools = await ResolveDynamicToolsAsync(cancellationToken).ConfigureAwait(false);
        if (dynamicTools.Count > 0)
        {
            describes = describes.Concat(dynamicTools.Select(t => t.Describe()));
        }

        var ordered = describes
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();

        try
        {
            var page = McpPagination.Page(ordered, cursor, _limits.ListPageSize, out var nextCursor);
            return SuccessResponse(
                request.Id,
                new McpToolsListResult { Tools = page, NextCursor = nextCursor },
                McpJsonContext.Default.McpToolsListResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }

    /// <summary>
    /// Resolves the runtime-published dynamic tools (#2483) from every registered
    /// <see cref="IMcpToolSource"/>, excluding any name that collides with a static
    /// tool (static wins) or a name already yielded by an earlier source (first
    /// wins). Returns an empty list when no source is composed — the default host.
    /// </summary>
    private async Task<IReadOnlyList<IMcpTool>> ResolveDynamicToolsAsync(CancellationToken cancellationToken)
    {
        if (_toolSources.Count == 0)
        {
            return [];
        }

        var merged = new List<IMcpTool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in _toolSources)
        {
            var tools = await source.GetToolsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var tool in tools)
            {
                if (_tools.ContainsKey(tool.Name) || !seen.Add(tool.Name))
                {
                    continue;
                }

                merged.Add(tool);
            }
        }

        return merged;
    }

    private async Task<IMcpTool?> ResolveDynamicToolAsync(string name, CancellationToken cancellationToken)
    {
        var dynamicTools = await ResolveDynamicToolsAsync(cancellationToken).ConfigureAwait(false);
        return dynamicTools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }

    private McpJsonRpcResponse ListPrompts(McpJsonRpcRequest request)
    {
        if (!TryReadCursor(request, out var cursor, out var error))
        {
            return error;
        }

        // McpPromptCatalog.List() already returns the catalog in stable name order.
        var ordered = McpPromptCatalog.List().Prompts;

        try
        {
            var page = McpPagination.Page(ordered, cursor, _limits.ListPageSize, out var nextCursor);
            return SuccessResponse(
                request.Id,
                new McpPromptsListResult { Prompts = page, NextCursor = nextCursor },
                McpJsonContext.Default.McpPromptsListResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }

    private static McpJsonRpcResponse GetPrompt(McpJsonRpcRequest request)
    {
        McpPromptsGetParams? parameters;
        try
        {
            parameters = ParseParams(request.Params, McpJsonContext.Default.McpPromptsGetParams);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(parameters?.Name))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("prompts/get requires a prompt name."));
        }

        try
        {
            // Unknown prompt names and missing required arguments both surface as
            // GeoprocessingValidationException → JSON-RPC invalid_params (-32602),
            // matching how tools/call maps the prompt name and argument shape.
            var result = McpPromptCatalog.Get(parameters.Name, parameters.Arguments);
            return SuccessResponse(request.Id, result, McpJsonContext.Default.McpPromptGetResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }

    private async Task<McpJsonRpcResponse> CallToolAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        // Per docs/developer/MCP_SERVER.md the authenticated principal gate
        // precedes any protocol-level param or tool-name validation so an
        // anonymous caller sees the `unauthenticated` reauthentication signal
        // regardless of whether the requested tool exists. Surfaces the error
        // through the same isError:true envelope tool-execution auth failures
        // already use so MCP clients can drive a single reauth flow.
        if (httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
        {
            // Emit the same honua.mcp.tool.call counter sample and
            // McpLog.AuthorizationDenied entry that concrete tool handlers emit
            // on failure, using `unknown` sentinels for tool name and workflow
            // family because the dispatcher rejected the call before resolving
            // the requested tool.
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", McpTelemetry.UnknownToolName),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error),
                new KeyValuePair<string, object?>("workflow_family", McpTelemetry.WorkflowFamily.Unknown));
            McpLog.AuthorizationDenied(_logger, "tools/call", authenticated: false);
            var authResult = McpToolHelpers.ErrorResult(
                new GeoprocessingAuthorizationException(requiresAuthentication: true));
            return SuccessResponse(request.Id, authResult, McpJsonContext.Default.McpToolsCallResult);
        }

        McpToolsCallParams? parameters;
        try
        {
            parameters = ParseParams(request.Params, McpJsonContext.Default.McpToolsCallParams);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(parameters?.Name))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("tools/call requires a tool name."));
        }

        if (!_tools.TryGetValue(parameters.Name, out var tool))
        {
            // Fall back to the runtime-published dynamic tools (#2483) before
            // declaring the name unknown, so a published operation tool advertised in
            // tools/list is callable. Static tools were checked first, so they win.
            tool = await ResolveDynamicToolAsync(parameters.Name, cancellationToken).ConfigureAwait(false);
            if (tool is null)
            {
                // MCP 2025-03-26 maps unknown tool names to -32602 invalid params; the
                // tool name is part of the tools/call `params` payload.
                return ErrorResponse(
                    request.Id,
                    McpErrorMapper.InvalidArgument($"Unknown MCP tool '{parameters.Name}'."));
            }
        }

        McpToolsCallResult result;
        try
        {
            result = await tool
                .InvokeAsync(httpContext, parameters.Arguments, cancellationToken)
                .ConfigureAwait(false);
            // Contract-first stubs return a structured `not_implemented` payload.
            // Tag telemetry with that status so dashboards can distinguish stubs
            // from functional tools without inspecting response bodies.
            var completionStatus = tool is IStubMcpTool
                ? McpTelemetry.Status.NotImplemented
                : McpTelemetry.Status.Ok;
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", tool.Name),
                new KeyValuePair<string, object?>("status", completionStatus),
                new KeyValuePair<string, object?>("workflow_family", tool.WorkflowFamily));
            McpLog.ToolCompleted(_logger, tool.Name, completionStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentionally generic: this is the top-level MCP tool-invocation boundary. Per MCP
        // 2025-03-26 tool-execution failures (auth, approval, validation, domain) must appear
        // inside the result with isError: true so standard clients can drive retry/re-auth flows
        // without parsing protocol-level JSON-RPC errors.
        catch (Exception ex)
        {
            result = McpToolHelpers.ErrorResult(ex);
            var code = ExtractErrorCode(result);
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", tool.Name),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error),
                new KeyValuePair<string, object?>("workflow_family", tool.WorkflowFamily));
            McpLog.ToolFailed(_logger, tool.Name, code, ex.Message);
            // Log the full exception for internal/unexpected failures so stack traces
            // are visible; domain exceptions (auth, validation, etc.) already carry
            // their message in the ToolFailed entry above and do not need a stack trace.
            if (string.Equals(code, McpErrorMapper.Codes.Internal, StringComparison.Ordinal))
            {
                McpLog.ToolFailedUnexpected(_logger, tool.Name, ex);
            }
        }

        return SuccessResponse(request.Id, result, McpJsonContext.Default.McpToolsCallResult);
    }

    private McpJsonRpcResponse ListResources(McpJsonRpcRequest request)
    {
        if (!TryReadCursor(request, out var cursor, out var error))
        {
            return error;
        }

        var ordered = _resources
            .SelectMany(r => r.Describe())
            .OrderBy(d => d.Uri, StringComparer.Ordinal)
            .ToList();

        try
        {
            var page = McpPagination.Page(ordered, cursor, _limits.ListPageSize, out var nextCursor);
            return SuccessResponse(
                request.Id,
                new McpResourcesListResult { Resources = page, NextCursor = nextCursor },
                McpJsonContext.Default.McpResourcesListResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }

    private McpJsonRpcResponse ListResourceTemplates(McpJsonRpcRequest request)
    {
        if (!TryReadCursor(request, out var cursor, out var error))
        {
            return error;
        }

        var ordered = _resources
            .SelectMany(r => r.DescribeTemplates())
            .OrderBy(d => d.UriTemplate, StringComparer.Ordinal)
            .ToList();

        try
        {
            var page = McpPagination.Page(ordered, cursor, _limits.ListPageSize, out var nextCursor);
            return SuccessResponse(
                request.Id,
                new McpResourceTemplatesListResult { ResourceTemplates = page, NextCursor = nextCursor },
                McpJsonContext.Default.McpResourceTemplatesListResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }

    /// <summary>
    /// Parses the optional opaque <c>cursor</c> from a paginated list request's
    /// <c>params</c>. Returns <c>false</c> with a populated
    /// <paramref name="errorResponse"/> when the params shape is malformed; a
    /// missing or null cursor is valid and yields <c>null</c>.
    /// </summary>
    private static bool TryReadCursor(
        McpJsonRpcRequest request,
        out string? cursor,
        out McpJsonRpcResponse errorResponse)
    {
        cursor = null;
        errorResponse = null!;
        try
        {
            cursor = ParseParams(request.Params, McpJsonContext.Default.McpListParams)?.Cursor;
            return true;
        }
        catch (GeoprocessingValidationException ex)
        {
            errorResponse = ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
            return false;
        }
    }

    private async Task<McpJsonRpcResponse> ReadResourceAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        // Authenticate at the dispatcher entry before URI parsing or handler
        // lookup so the contract's `unauthenticated` signal reaches anonymous
        // callers even for malformed params or unknown URIs, rather than
        // leaking the protocol-level `invalid_argument`/`not_found` surface.
        if (httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
        {
            // Emit the same honua.mcp.resource.read counter sample and
            // McpLog.AuthorizationDenied entry that concrete resource handlers
            // emit on failure, using the `unknown` family sentinel because the
            // dispatcher rejected the read before resolving the URI family.
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", McpTelemetry.ResourceFamily.Unknown),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error));
            McpLog.AuthorizationDenied(_logger, "resources/read", authenticated: false);
            return ErrorResponse(request.Id, McpErrorMapper.Unauthenticated());
        }

        McpResourcesReadParams? parameters;
        try
        {
            parameters = ParseParams(request.Params, McpJsonContext.Default.McpResourcesReadParams);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(parameters?.Uri))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("resources/read requires a URI."));
        }

        var handler = _resources.FirstOrDefault(r => r.CanHandle(parameters.Uri));
        if (handler is null)
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.ResourceNotFound($"Unknown MCP resource '{parameters.Uri}'."));
        }

        // Resolve the family from the requested URI so multi-root handlers
        // (e.g. the promotion-surface index) report per-root families rather
        // than collapsing into one rolled-up tag on the counter.
        var resourceFamily = handler.ResolveFamily(parameters.Uri);
        try
        {
            var result = await handler
                .ReadAsync(httpContext, parameters.Uri, cancellationToken)
                .ConfigureAwait(false);

            // Chunk large documents (job results, catalogs) into windowed pages so
            // a single resources/read response stays bounded; small documents are
            // returned verbatim with no nextCursor. An invalid cursor surfaces as
            // the same invalid_argument the list methods use.
            try
            {
                var page = McpPagination.Chunk(
                    result.Contents,
                    parameters.Cursor,
                    _limits.MaxResourceReadChars,
                    out var nextCursor);
                result = new McpResourcesReadResult { Contents = page, NextCursor = nextCursor };
            }
            catch (GeoprocessingValidationException ex)
            {
                McpTelemetry.ResourceReadCount.Add(
                    1,
                    new KeyValuePair<string, object?>("resource_family", resourceFamily),
                    new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error));
                return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
            }

            // Contract-first stub resources return `not_implemented` envelopes.
            // Tag the counter accordingly so dashboards can separate stub reads
            // from functional reads.
            var completionStatus = handler is IStubMcpResource
                ? McpTelemetry.Status.NotImplemented
                : McpTelemetry.Status.Ok;
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", resourceFamily),
                new KeyValuePair<string, object?>("status", completionStatus));
            return SuccessResponse(request.Id, result, McpJsonContext.Default.McpResourcesReadResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = McpErrorMapper.Map(ex);
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", resourceFamily),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error));
            // Log the full exception for internal failures so stack traces are visible.
            if (string.Equals(error.Data?.Code, McpErrorMapper.Codes.Internal, StringComparison.Ordinal))
            {
                McpLog.ResourceReadFailed(_logger, resourceFamily, parameters.Uri, ex);
            }
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

    private static string ExtractErrorCode(McpToolsCallResult result)
    {
        if (result.StructuredContent is null)
        {
            return McpErrorMapper.Codes.Internal;
        }

        var structured = result.StructuredContent.Value;
        if (structured.ValueKind == JsonValueKind.Object
            && structured.TryGetProperty("code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String)
        {
            return codeElement.GetString() ?? McpErrorMapper.Codes.Internal;
        }

        return McpErrorMapper.Codes.Internal;
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
