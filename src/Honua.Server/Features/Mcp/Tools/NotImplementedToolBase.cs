// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// Base implementation for MCP tools that are contract-first stubs. Returns a
/// structured <see cref="McpNotImplementedOutput"/> with enough information for
/// operators to understand the contract and the unblock path while still
/// exercising authentication and telemetry.
/// </summary>
internal abstract class NotImplementedToolBase : IMcpTool
{
    private readonly ILogger _logger;

    protected NotImplementedToolBase(ILogger logger)
    {
        _logger = logger;
    }

    public abstract string Name { get; }

    public abstract string WorkflowFamily { get; }

    protected abstract string Description { get; }

    protected abstract string BlockedBy { get; }

    protected abstract string Contract { get; }

    protected virtual IReadOnlyList<string> NextSteps { get; } = [];

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = McpToolSchemas.EmptyObjectSchema
    };

    public Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity(Name);
        McpLog.StubToolInvoked(_logger, Name, BlockedBy);

        _ = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var output = new McpNotImplementedOutput
        {
            Status = "not_implemented",
            Tool = Name,
            BlockedBy = BlockedBy,
            Contract = Contract,
            NextSteps = NextSteps
        };

        return Task.FromResult(McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpNotImplementedOutput));
    }
}
