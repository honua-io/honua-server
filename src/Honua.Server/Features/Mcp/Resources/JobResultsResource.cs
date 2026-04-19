// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://jobs/{jobId}/results</c>. Delegates to
/// <see cref="IGeoprocessingJobService.GetJobResultsAsync"/> so the MCP surface
/// preserves the canonical result-package lifecycle: missing jobs return
/// <c>not_found</c>, non-terminal jobs return
/// <c>failed_precondition</c>, and successful package reads serialize the
/// domain <see cref="AnalysisResultPackage"/> into the published MCP envelope.
/// </summary>
internal sealed class JobResultsResource : IMcpResource
{
    public const string Template =
        McpResourceUris.JobsPrefix + "{jobId}" + McpResourceUris.JobResultsSuffix;

    private readonly IGeoprocessingJobService _jobService;

    private readonly ILogger<JobResultsResource> _logger;

    public JobResultsResource(
        IGeoprocessingJobService jobService,
        ILogger<JobResultsResource> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.JobResults;

    public IReadOnlyList<McpResourceDescriptor> Describe() => new[]
    {
        new McpResourceDescriptor
        {
            Uri = Template,
            Name = "Geoprocessing job results",
            Description = "AnalysisResultPackage envelope for a terminal job, delegated through the canonical geoprocessing job service.",
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

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetJobResults");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var jobId = uri.Substring(
            McpResourceUris.JobsPrefix.Length,
            uri.Length - McpResourceUris.JobsPrefix.Length - McpResourceUris.JobResultsSuffix.Length);
        McpLog.ResourceRead(_logger, Family, uri);

        var package = await _jobService
            .GetJobResultsAsync(jobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var resource = ToResource(jobId, package);
        var result = McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpJobResultsResource);
        return result;
    }

    private static McpJobResultsResource ToResource(string jobId, AnalysisResultPackage package) => new()
    {
        JobId = jobId,
        ResultPackageId = package.ResultPackageId,
        Status = package.Status.ToString(),
        Summary = new McpResultSummary
        {
            Title = package.Summary.Title,
            Description = package.Summary.Description
        },
        Artifacts = package.Artifacts
            .Select(artifact => new McpArtifactRef
            {
                ArtifactId = artifact.ArtifactId,
                Kind = artifact.Kind.ToString(),
                Label = artifact.Label,
                Uri = artifact.Uri,
                ContentType = artifact.ContentType,
                Metadata = artifact.Metadata
            })
            .ToList(),
        WorkspaceRefs = package.WorkspaceRefs
            .Select(workspace => new McpWorkspaceRef
            {
                WorkspaceId = workspace.WorkspaceId,
                Kind = workspace.Kind.ToString(),
                Label = workspace.Label,
                Uri = workspace.Uri,
                ExpiresAt = workspace.ExpiresAt,
                ResourceUri = McpResourceUris.WorkspaceUri(workspace.WorkspaceId)
            })
            .ToList(),
        MapPackageId = package.MapPackageId,
        AppPackageId = package.AppPackageId,
        Assumptions = package.Assumptions,
        Provenance = new McpProvenance
        {
            Sources = package.Provenance.Sources
                .Select(source => new McpProvenanceSource
                {
                    SourceId = source.SourceId,
                    Version = source.Version,
                    Description = source.Description
                })
                .ToList(),
            ProcessDefinitions = package.Provenance.ProcessDefinitions,
            Assumptions = package.Provenance.Assumptions,
            ClarificationsAsked = package.Provenance.ClarificationsAsked,
            ClarificationsAnswered = package.Provenance.ClarificationsAnswered,
            ExecutedAt = package.Provenance.ExecutedAt,
            GeneratedArtifactIds = package.Provenance.GeneratedArtifactIds
        },
        Errors = package.Errors
            .Select(error => new McpGeoprocessingError
            {
                Kind = error.Kind.ToString(),
                Message = error.Message,
                StepId = error.StepId,
                Violations = error.Violations?
                    .Select(violation => new McpValidationViolation
                    {
                        Code = violation.Code,
                        Message = violation.Message,
                        FieldPath = violation.FieldPath
                    })
                    .ToList()
            })
            .ToList()
    };
}
