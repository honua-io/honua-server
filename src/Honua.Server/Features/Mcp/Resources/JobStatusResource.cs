// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://jobs/{jobId}</c>. Delegates to
/// <see cref="IGeoprocessingJobService.GetJobAsync"/> for authorization and
/// record lookup, then serializes the record as a structured MCP job resource.
/// </summary>
internal sealed class JobStatusResource : IMcpResource
{
    public const string Template = McpResourceUris.JobsPrefix + "{jobId}";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<JobStatusResource> _logger;

    public JobStatusResource(IGeoprocessingJobService jobService, ILogger<JobStatusResource> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Jobs;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Geoprocessing job status",
            Description = "Execution job lifecycle record including status, phase, percent complete, and warnings.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.JobsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.JobsPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetJob");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var jobId = uri[McpResourceUris.JobsPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var record = await _jobService
            .GetJobAsync(jobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var resource = new McpJobResource
        {
            JobId = record.OperationId,
            Status = record.Status.ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            CompletedAt = record.CompletedAt,
            PercentComplete = record.PercentComplete,
            CurrentPhase = record.CurrentPhase,
            ErrorMessage = record.ErrorMessage,
            Warnings = record.Warnings,
            ResultsUri = McpResourceUris.JobResultsUri(record.OperationId)
        };

        return McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpJobResource);
    }
}
