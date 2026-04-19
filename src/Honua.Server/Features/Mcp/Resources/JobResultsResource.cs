// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://jobs/{jobId}/results</c>. Returns a
/// <c>not_implemented</c> stub envelope until result storage ships with the
/// execution engine. The URI, MIME type, and
/// <see cref="AnalysisResultPackage"/>-shaped payload are stable so clients
/// can bind today; the real package fields (artifacts, workspaces,
/// <c>mapPackageId</c>, provenance) fill in once
/// <c>IGeoprocessingJobService.GetJobResultsAsync</c> can serve packages.
/// </summary>
internal sealed class JobResultsResource : IMcpResource
{
    public const string Template =
        McpResourceUris.JobsPrefix + "{jobId}" + McpResourceUris.JobResultsSuffix;

    private const string NotImplementedReason =
        "Result package retrieval depends on the execution engine's result store; slated for a follow-up ticket in the geoprocessing epic.";

    private readonly ILogger<JobResultsResource> _logger;

    public JobResultsResource(ILogger<JobResultsResource> logger)
    {
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.JobResults;

    public IReadOnlyList<McpResourceDescriptor> Describe() => new[]
    {
        new McpResourceDescriptor
        {
            Uri = Template,
            Name = "Geoprocessing job results (stub)",
            Description = "AnalysisResultPackage envelope for a completed job. Contract stub pending result storage in the execution engine.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.JobsPrefix, StringComparison.Ordinal) ||
            !uri.EndsWith(McpResourceUris.JobResultsSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var idSegment = uri.AsSpan(
            McpResourceUris.JobsPrefix.Length,
            uri.Length - McpResourceUris.JobsPrefix.Length - McpResourceUris.JobResultsSuffix.Length);
        return idSegment.Length > 0 && !idSegment.Contains('/');
    }

    public Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetJobResults");
        _ = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var jobId = uri.Substring(
            McpResourceUris.JobsPrefix.Length,
            uri.Length - McpResourceUris.JobsPrefix.Length - McpResourceUris.JobResultsSuffix.Length);
        McpLog.ResourceRead(_logger, Family, uri);

        var resource = new McpJobResultsResource
        {
            JobId = jobId,
            Status = "not_implemented",
            NotImplementedReason = NotImplementedReason
        };

        var result = McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpJobResultsResource);
        return Task.FromResult(result);
    }
}
