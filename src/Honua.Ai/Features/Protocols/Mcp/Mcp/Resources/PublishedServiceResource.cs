// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://published-services/{serviceId}</c>. Reads a
/// <see cref="PublishedServiceRecord"/> through the canonical promotion store,
/// or an ordinary layer publication through the persisted Metadata v2 graph.
/// Promotion provenance is surfaced only when a promotion record exists.
/// Authorization starts with the
/// <see cref="OperatorResourceType.PublishedService"/> grant evaluated by
/// <see cref="IGeoprocessingJobService.EnsureCallerAuthorizedAsync(System.Security.Claims.ClaimsPrincipal, OperatorResourceType, OperatorOperation, System.Threading.CancellationToken)"/>.
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
            Description = "Published service from the canonical metadata catalog or managed promotion lifecycle, with provenance only when persisted.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri) => McpResourceUris.TryParsePublishedServiceUri(uri, out _);

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetPublishedService");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.PublishedService, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        if (!McpResourceUris.TryParsePublishedServiceUri(uri, out var serviceId))
        {
            throw new GeoprocessingValidationException("The published service URI is not canonically encoded.");
        }

        McpLog.ResourceRead(_logger, Family, uri);

        var record = await _services.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var graphProvider = httpContext.RequestServices.GetService<IMetadataV2GraphProvider>();
        if (graphProvider is not null)
        {
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await PublishedMetadataCatalogProjection.FindAsync(
                httpContext, snapshot, serviceId, refuseDenied: true, cancellationToken).ConfigureAwait(false);
            if (catalog is not null)
            {
                if (record is not null)
                {
                    throw new GeoprocessingValidationException(
                        $"Published service identity '{serviceId}' is shared by a promotion and a catalog publication.");
                }

                McpLog.PublishedServiceRead(_logger, serviceId, catalog.Service.Status?.Lifecycle.ToString() ?? "Active");
                return McpResourceHelpers.SingleJsonContent(
                    uri, catalog.ToDetail(), McpJsonContext.Default.McpPublishedServiceView);
            }
        }

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
