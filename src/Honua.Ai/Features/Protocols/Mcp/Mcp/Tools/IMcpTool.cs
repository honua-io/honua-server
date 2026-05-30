// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Contract for a single MCP tool exposed through the operator surface.
/// Each tool is a thin adapter over a canonical domain service and is
/// responsible for its own input deserialization, authorization delegation,
/// and structured output.
/// </summary>
internal interface IMcpTool
{
    /// <summary>
    /// The tool name published in <c>tools/list</c>. Names must match the
    /// geospatial-mcp taxonomy entries in AI_OPERATOR_CONTRACT.md.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Workflow-family tag used for telemetry, sourced from
    /// <see cref="McpTelemetry.WorkflowFamily"/>.
    /// </summary>
    string WorkflowFamily { get; }

    /// <summary>
    /// Describes the tool to clients for <c>tools/list</c>, including its
    /// JSON-schema input contract.
    /// </summary>
    McpToolDescriptor Describe();

    /// <summary>
    /// Invokes the tool. Implementations throw domain exceptions on failure;
    /// <see cref="McpOperatorSurface"/> converts those exceptions into JSON-RPC
    /// error envelopes via <see cref="McpErrorMapper"/>.
    /// </summary>
    Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken);
}
