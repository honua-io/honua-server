// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Mcp.Tools;

/// <summary>
/// Marker interface applied to MCP tools whose backing service has not yet
/// shipped and which therefore return structured <c>not_implemented</c>
/// output. <see cref="McpOperatorSurface"/> uses this signal to emit
/// <see cref="McpTelemetry.Status.NotImplemented"/> in counters and completion
/// logs so dashboards can distinguish contract stubs from functional tools.
/// </summary>
internal interface IStubMcpTool
{
}
