// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://workspaces/{workspaceId}</c>. Returns
/// <c>not_implemented</c> metadata until the workspace store lands; the contract
/// and URI template are stable so clients can bind today.
/// </summary>
internal sealed class WorkspaceResource : IMcpResource, IStubMcpResource
{
    public const string Template = McpResourceUris.WorkspacesPrefix + "{workspaceId}";

    private const string NotImplementedReason =
        "Workspace resource reads depend on the workspace store; slated for a follow-up ticket in the geoprocessing epic.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<WorkspaceResource> _logger;

    public WorkspaceResource(IGeoprocessingJobService jobService, ILogger<WorkspaceResource> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Workspaces;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Workspace (stub)",
            Description = "Workspace metadata for a referenced workspace id. Contract stub pending workspace store.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.WorkspacesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.WorkspacesPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetWorkspace");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Workspace, OperatorOperation.Read);

        var workspaceId = uri[McpResourceUris.WorkspacesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var resource = new McpWorkspaceResource
        {
            WorkspaceId = workspaceId,
            Kind = string.Empty,
            Label = string.Empty,
            Status = "not_implemented",
            NotImplementedReason = NotImplementedReason
        };

        var result = McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpWorkspaceResource);
        return Task.FromResult(result);
    }
}
