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
    /// Resolves the resource-family tag for counter telemetry on the specific
    /// URI being read. Defaults to <see cref="Family"/>; multi-root handlers
    /// override this to return a URI-specific family so dashboards preserve
    /// per-root success/error observability instead of collapsing to a single
    /// rolled-up tag.
    /// </summary>
    string ResolveFamily(string uri) => Family;

    /// <summary>
    /// Concrete URI descriptors advertised via <c>resources/list</c>. Resources
    /// whose only addressable form is a template (e.g. <c>honua://jobs/{jobId}</c>)
    /// return an empty list here and publish themselves through
    /// <see cref="DescribeTemplates"/> instead.
    /// </summary>
    IReadOnlyList<McpResourceDescriptor> Describe();

    /// <summary>
    /// URI-template descriptors advertised via <c>resources/templates/list</c> per
    /// MCP 2025-03-26. Fixed-path resources return an empty list.
    /// </summary>
    IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates();

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
