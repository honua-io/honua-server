// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// Contract for an MCP resource family exposed through the operator surface.
/// Resources handle prefix-based URI routing (<c>honua://jobs/</c>,
/// <c>honua://workspaces/</c>, …) so the dispatcher can delegate reads without
/// parsing query strings or path components itself.
/// </summary>
internal interface IMcpResource
{
    /// <summary>
    /// Resource-family tag used for telemetry and descriptors, sourced from
    /// <see cref="McpTelemetry.ResourceFamily"/>.
    /// </summary>
    string Family { get; }

    /// <summary>
    /// Static descriptors this resource exposes via <c>resources/list</c>.
    /// Implementations that cover a URI template (e.g. <c>honua://jobs/{jobId}</c>)
    /// return a single template descriptor; fixed-path resources (catalog) return
    /// their concrete URI.
    /// </summary>
    IReadOnlyList<McpResourceDescriptor> Describe();

    /// <summary>
    /// Returns <c>true</c> when this resource should handle the supplied URI.
    /// </summary>
    bool CanHandle(string uri);

    /// <summary>
    /// Reads the resource at <paramref name="uri"/>. Implementations throw
    /// domain exceptions on failure; <see cref="McpOperatorSurface"/> converts
    /// them to JSON-RPC errors via <see cref="McpErrorMapper"/>.
    /// </summary>
    Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken);
}
