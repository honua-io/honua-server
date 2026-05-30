// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// Marker interface applied to MCP resources whose backing service has not yet
/// shipped and which therefore return structured <c>not_implemented</c>
/// output. <see cref="McpOperatorSurface"/> uses this signal to tag the
/// <c>honua.mcp.resource.read</c> counter with
/// <see cref="McpTelemetry.Status.NotImplemented"/> so dashboards can
/// distinguish contract stubs from functional resource reads.
/// </summary>
internal interface IStubMcpResource
{
}
