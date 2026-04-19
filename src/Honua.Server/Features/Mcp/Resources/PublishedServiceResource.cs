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
    private readonly IPublishIntentStore _intents;
    private readonly IDeploymentStore _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<PublishedServiceResource> _logger;

    public PublishedServiceResource(
        IPublishedServiceStore services,
        IPublishIntentStore intents,
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<PublishedServiceResource> logger)
    {
        _services = services;
        _intents = intents;
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
        var record = await _services.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw new GeoprocessingNotFoundException($"Published service '{serviceId}' not found.");
        }

        var intent = await _intents.GetAsync(record.IntentId, cancellationToken).ConfigureAwait(false);
        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.PublishedService, serviceId, cancellationToken)
            .ConfigureAwait(false);

        McpLog.PublishedServiceRead(_logger, serviceId, record.Status.ToString());

        var view = ToView(record, intent, deployments);
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPublishedServiceView);
    }

    internal static McpPublishedServiceView ToView(
        PublishedServiceRecord record,
        PublishIntent? intent,
        IReadOnlyList<Deployment> deployments)
    {
        var parentDeployment = deployments.Count > 0
            ? McpResourceUris.DeploymentUri(deployments[0].DeploymentId)
            : null;

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
            Provenance = new McpHostedProvenance
            {
                OriginatingIntentId = record.IntentId,
                ResultPackageId = intent?.SourceKind == PublishSourceKind.ResultPackage ? intent.SourceId : null,
                PublishedServiceResourceUri = McpResourceUris.PublishedServiceUri(record.ServiceId),
                ParentDeploymentResourceUri = parentDeployment
            }
        };
    }
}
