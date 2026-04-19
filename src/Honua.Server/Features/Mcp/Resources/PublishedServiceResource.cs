// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://published-services/{serviceId}</c>. Reads a
/// <see cref="PublishedServiceRecord"/> through the canonical publishing store
/// and surfaces provenance edges back to the originating intent, hosted
/// deployments, and source result package. Authorization defers to the
/// <see cref="OperatorResourceType.PublishedService"/> grant evaluated by
/// <see cref="IGeoprocessingJobService.EnsureCallerAuthorized"/>.
/// </summary>
internal sealed class PublishedServiceResource : IMcpResource
{
    public const string Template = McpResourceUris.PublishedServicesPrefix + "{serviceId}";

    private readonly IPublishedServiceStore _services;
    private readonly IDeploymentStore _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<PublishedServiceResource> _logger;

    public PublishedServiceResource(
        IPublishedServiceStore services,
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<PublishedServiceResource> logger)
    {
        _services = services;
        _deployments = deployments;
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.PublishedServices;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Published service",
            Description = "Managed published service produced by the promotion lifecycle, with provenance edges back to the originating intent and hosted deployments.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.PublishedServicesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.PublishedServicesPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetPublishedService");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.PublishedService, OperatorOperation.Read);

        var serviceId = uri[McpResourceUris.PublishedServicesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var record = await _services.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw new GeoprocessingNotFoundException($"Published service '{serviceId}' not found.");
        }

        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.PublishedService, serviceId, cancellationToken)
            .ConfigureAwait(false);
        deployments = PackageViewFactory.FilterPublished(deployments);

        McpLog.PublishedServiceRead(_logger, serviceId, record.Status.ToString());

        var view = ToView(record, deployments);
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPublishedServiceView);
    }

    internal static McpPublishedServiceView ToView(
        PublishedServiceRecord record,
        IReadOnlyList<Deployment> deployments)
    {
        // Sort by deployment id so multi-deployment services produce stable output
        // regardless of the store's reverse-lookup ordering. A published service has no
        // canonical single parent deployment; DeploymentResourceUris is the full set.
        var deploymentUris = deployments
            .Select(deployment => McpResourceUris.DeploymentUri(deployment.DeploymentId))
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .ToList();

        return new McpPublishedServiceView
        {
            ServiceId = record.ServiceId,
            ResourceUri = McpResourceUris.PublishedServiceUri(record.ServiceId),
            Status = record.Status.ToString(),
            SourceKind = record.SourceKind.ToString(),
            SourceId = record.SourceId,
            TargetKind = record.TargetKind.ToString(),
            Endpoint = record.Endpoint,
            PublishedAt = record.PublishedAt,
            LastRefreshedAt = record.LastRefreshedAt,
            UpdatedAt = record.UpdatedAt,
            Etag = PromotionSurfaceEtag.ForPublishedService(record),
            Artifacts = record.Artifacts
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
            Warnings = record.Warnings,
            DeploymentCount = deploymentUris.Count,
            DeploymentResourceUris = deploymentUris,
            Provenance = new McpHostedProvenance
            {
                OriginatingIntentId = record.IntentId,
                // Source-package provenance is persisted on the canonical service record
                // (SourceKind/SourceId are required fields), so deriving from `record`
                // keeps the provenance edge stable even when the intent row is missing
                // or stale — the intent is a planning artifact and can be cleaned up
                // independently of the published service.
                ResultPackageId = record.SourceKind == PublishSourceKind.ResultPackage ? record.SourceId : null,
                PublishedServiceResourceUri = McpResourceUris.PublishedServiceUri(record.ServiceId)
            }
        };
    }
}
