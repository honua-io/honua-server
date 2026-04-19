// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://jobs/{jobId}/results</c>. Returns the canonical
/// <see cref="AnalysisResultPackage"/> with artifact, workspace, provenance,
/// and error details. The map-package identifier flows through directly from
/// <c>AnalysisResultPackage.MapPackageId</c>, satisfying the map-package output
/// acceptance criterion for this ticket.
/// </summary>
internal sealed class JobResultsResource : IMcpResource
{
    public const string Template =
        McpResourceUris.JobsPrefix + "{jobId}" + McpResourceUris.JobResultsSuffix;

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<JobResultsResource> _logger;

    public JobResultsResource(IGeoprocessingJobService jobService, ILogger<JobResultsResource> logger)
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
            Description = "AnalysisResultPackage for a completed job — artifacts, workspace refs, map package id, provenance, and errors.",
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

        var resource = ToResource(package);
        return McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpJobResultsResource);
    }

    private static McpJobResultsResource ToResource(AnalysisResultPackage package) => new()
    {
        ResultPackageId = package.ResultPackageId,
        Status = package.Status.ToString(),
        Summary = new McpResultSummary
        {
            Title = package.Summary.Title,
            Description = package.Summary.Description
        },
        Artifacts = package.Artifacts
            .Select(a => new McpArtifactRef
            {
                ArtifactId = a.ArtifactId,
                Kind = a.Kind.ToString(),
                Label = a.Label,
                Uri = a.Uri,
                ContentType = a.ContentType,
                Metadata = a.Metadata
            })
            .ToList(),
        WorkspaceRefs = package.WorkspaceRefs
            .Select(w => new McpWorkspaceRef
            {
                WorkspaceId = w.WorkspaceId,
                Kind = w.Kind.ToString(),
                Label = w.Label,
                Uri = w.Uri,
                ExpiresAt = w.ExpiresAt,
                ResourceUri = McpResourceUris.WorkspaceUri(w.WorkspaceId)
            })
            .ToList(),
        MapPackageId = package.MapPackageId,
        AppPackageId = package.AppPackageId,
        Assumptions = package.Assumptions,
        Provenance = new McpProvenance
        {
            Sources = package.Provenance.Sources
                .Select(s => new McpProvenanceSource
                {
                    SourceId = s.SourceId,
                    Version = s.Version,
                    Description = s.Description
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
            .Select(e => new McpGeoprocessingError
            {
                Kind = e.Kind.ToString(),
                Message = e.Message,
                StepId = e.StepId,
                Violations = e.Violations?
                    .Select(v => new McpValidationViolation
                    {
                        Code = v.Code,
                        Message = v.Message,
                        FieldPath = v.FieldPath
                    })
                    .ToList()
            })
            .ToList()
    };
}
