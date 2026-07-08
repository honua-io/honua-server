// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for the fixed <c>honua://ops/health</c> operational-health
/// snapshot.
/// </summary>
internal sealed class OpsHealthResource(ILogger<OpsHealthResource> logger) : IMcpResource
{
    public const string Uri = McpResourceUris.OpsHealth;

    public string Family => McpTelemetry.ResourceFamily.OpsHealth;

    public IReadOnlyList<McpResourceDescriptor> Describe() =>
    [
        new McpResourceDescriptor
        {
            Uri = Uri,
            Name = "Ops health",
            Description = "Read-only consolidated operational-health snapshot.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    ];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) => string.Equals(uri, Uri, StringComparison.Ordinal);

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ReadOpsHealthResource");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        McpLog.ResourceRead(logger, Family, uri);

        var payload = await ResolveReader(httpContext)
            .GetOpsHealthAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        return McpResourceHelpers.SingleJsonContent(uri, payload);
    }

    private static IMcpOpsObservabilityReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpOpsObservabilityReader>();
}

/// <summary>
/// MCP resource for the fixed <c>honua://ops/findings</c> deterministic
/// findings list.
/// </summary>
internal sealed class OpsFindingsResource(ILogger<OpsFindingsResource> logger) : IMcpResource
{
    public const string Uri = McpResourceUris.OpsFindings;

    public string Family => McpTelemetry.ResourceFamily.OpsFindings;

    public IReadOnlyList<McpResourceDescriptor> Describe() =>
    [
        new McpResourceDescriptor
        {
            Uri = Uri,
            Name = "Ops findings",
            Description = "Read-only deterministic operational findings.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    ];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) => string.Equals(uri, Uri, StringComparison.Ordinal);

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ReadOpsFindingsResource");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        McpLog.ResourceRead(logger, Family, uri);

        var payload = await ResolveReader(httpContext)
            .GetOpsFindingsAsync(principal, new McpOpsFindingsArgument(), cancellationToken)
            .ConfigureAwait(false);

        return McpResourceHelpers.SingleJsonContent(uri, payload);
    }

    private static IMcpOpsObservabilityReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpOpsObservabilityReader>();
}
