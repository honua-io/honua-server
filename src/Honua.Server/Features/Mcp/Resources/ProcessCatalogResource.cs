// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://catalog/processes</c>. Returns a stable empty
/// catalog envelope tagged <c>not_implemented</c> until the process catalog
/// service ships. Clients can bind to the URI and MIME type today.
/// </summary>
internal sealed class ProcessCatalogResource : IMcpResource, IStubMcpResource
{
    public const string Uri = McpResourceUris.CatalogProcesses;

    private const string NotImplementedReason =
        "Process catalog reads depend on the catalog service; slated for a follow-up ticket in the geoprocessing epic.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ProcessCatalogResource> _logger;

    public ProcessCatalogResource(IGeoprocessingJobService jobService, ILogger<ProcessCatalogResource> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Catalog;

    public IReadOnlyList<McpResourceDescriptor> Describe() => new[]
    {
        new McpResourceDescriptor
        {
            Uri = Uri,
            Name = "Process catalog (stub)",
            Description = "Catalog of processes advertised by this server. Contract stub pending catalog service.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) => string.Equals(uri, Uri, StringComparison.Ordinal);

    public Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetProcessCatalog");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Catalog, OperatorOperation.Discover);
        McpLog.ResourceRead(_logger, Family, uri);

        var resource = new McpProcessCatalogResource
        {
            CatalogVersion = string.Empty,
            Status = "not_implemented",
            Processes = [],
            NotImplementedReason = NotImplementedReason
        };

        var result = McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpProcessCatalogResource);
        return Task.FromResult(result);
    }
}
