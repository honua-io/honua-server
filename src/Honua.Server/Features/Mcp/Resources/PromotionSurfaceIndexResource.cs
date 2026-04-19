// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource handling the four list-root URIs that enumerate promotion
/// surfaces without a trailing identifier:
/// <c>honua://published-services</c>, <c>honua://deployments</c>,
/// <c>honua://map-packages</c>, and <c>honua://app-packages</c>. Each returns
/// a capped summary list drawn from the canonical store — active published
/// services, active deployments, or packages derived from the deployment
/// reverse-lookup. The cap (<see cref="DefaultPageSize"/>) is applied so agents
/// never download unbounded catalogs; a <c>truncated</c> flag signals when
/// results were clipped.
/// </summary>
internal sealed class PromotionSurfaceIndexResource : IMcpResource
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private readonly IPublishedServiceStore _services;
    private readonly IDeploymentStore _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<PromotionSurfaceIndexResource> _logger;
    private readonly int _pageSize;

    public PromotionSurfaceIndexResource(
        IPublishedServiceStore services,
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<PromotionSurfaceIndexResource> logger)
        : this(services, deployments, jobService, logger, DefaultPageSize)
    {
    }

    internal PromotionSurfaceIndexResource(
        IPublishedServiceStore services,
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<PromotionSurfaceIndexResource> logger,
        int pageSize)
    {
        _services = services;
        _deployments = deployments;
        _jobService = jobService;
        _logger = logger;
        _pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
    }

    public string Family => McpTelemetry.ResourceFamily.PromotionIndex;

    public IReadOnlyList<McpResourceDescriptor> Describe() => new[]
    {
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.PublishedServicesRoot,
            Name = "Published services index",
            Description = "Active published services produced by the promotion lifecycle.",
            MimeType = McpResourceHelpers.JsonMimeType
        },
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.DeploymentsRoot,
            Name = "Deployments index",
            Description = "Active deployments hosted by the promotion lifecycle.",
            MimeType = McpResourceHelpers.JsonMimeType
        },
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.MapPackagesRoot,
            Name = "Map packages index",
            Description = "Map packages visible through currently-active deployments.",
            MimeType = McpResourceHelpers.JsonMimeType
        },
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.AppPackagesRoot,
            Name = "App packages index",
            Description = "App packages visible through currently-active deployments.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) =>
        string.Equals(uri, McpResourceUris.PublishedServicesRoot, StringComparison.Ordinal) ||
        string.Equals(uri, McpResourceUris.DeploymentsRoot, StringComparison.Ordinal) ||
        string.Equals(uri, McpResourceUris.MapPackagesRoot, StringComparison.Ordinal) ||
        string.Equals(uri, McpResourceUris.AppPackagesRoot, StringComparison.Ordinal);

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        if (string.Equals(uri, McpResourceUris.PublishedServicesRoot, StringComparison.Ordinal))
        {
            return await ReadPublishedServiceIndexAsync(principal, uri, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(uri, McpResourceUris.DeploymentsRoot, StringComparison.Ordinal))
        {
            return await ReadDeploymentIndexAsync(principal, uri, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(uri, McpResourceUris.MapPackagesRoot, StringComparison.Ordinal))
        {
            return await ReadPackageIndexAsync(
                principal, uri, DeploymentSourceKind.MapPackage,
                "map_package", McpResourceUris.MapPackageUri, cancellationToken).ConfigureAwait(false);
        }

        return await ReadPackageIndexAsync(
            principal, uri, DeploymentSourceKind.AppPackage,
            "app_package", McpResourceUris.AppPackageUri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpResourcesReadResult> ReadPublishedServiceIndexAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListPublishedServices");
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.PublishedService, OperatorOperation.Read);

        var records = await _services.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var truncated = records.Count > _pageSize;
        var items = records
            .Take(_pageSize)
            .Select(record => new McpPublishedServiceSummary
            {
                ServiceId = record.ServiceId,
                ResourceUri = McpResourceUris.PublishedServiceUri(record.ServiceId),
                Status = record.Status.ToString(),
                TargetKind = record.TargetKind.ToString(),
                UpdatedAt = record.UpdatedAt,
                Etag = PromotionSurfaceEtag.ForPublishedService(record)
            })
            .ToList();

        McpLog.PromotionListRead(_logger, McpTelemetry.ResourceFamily.PublishedServices, items.Count, truncated);

        var view = new McpPublishedServiceListView
        {
            ResourceUri = uri,
            Count = items.Count,
            Truncated = truncated,
            Items = items
        };
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPublishedServiceListView);
    }

    private async Task<McpResourcesReadResult> ReadDeploymentIndexAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListDeployments");
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.Deployment, OperatorOperation.Read);

        var records = await _deployments.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var truncated = records.Count > _pageSize;
        var items = records
            .Take(_pageSize)
            .Select(deployment => new McpDeploymentSummary
            {
                DeploymentId = deployment.DeploymentId,
                ResourceUri = McpResourceUris.DeploymentUri(deployment.DeploymentId),
                Status = deployment.Status.ToString(),
                PublicationState = deployment.PublicationState.ToString(),
                SourceKind = deployment.Source.Kind.ToString(),
                TargetId = deployment.Target.TargetId,
                UpdatedAt = deployment.UpdatedAt,
                Etag = PromotionSurfaceEtag.ForDeployment(deployment)
            })
            .ToList();

        McpLog.PromotionListRead(_logger, McpTelemetry.ResourceFamily.Deployments, items.Count, truncated);

        var view = new McpDeploymentListView
        {
            ResourceUri = uri,
            Count = items.Count,
            Truncated = truncated,
            Items = items
        };
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpDeploymentListView);
    }

    private async Task<McpResourcesReadResult> ReadPackageIndexAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        string uri,
        DeploymentSourceKind packageKind,
        string packageKindTag,
        Func<string, string> packageUriFactory,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity(packageKind == DeploymentSourceKind.MapPackage
            ? "ListMapPackages"
            : "ListAppPackages");
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.Package, OperatorOperation.Read);

        var deployments = await _deployments.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var grouped = deployments
            .Where(deployment => deployment.Source.Kind == packageKind)
            .GroupBy(deployment => deployment.Source.SourceId, StringComparer.Ordinal)
            .ToList();

        var truncated = grouped.Count > _pageSize;
        var items = grouped
            .Take(_pageSize)
            .Select(group => new McpPackageSummary
            {
                PackageId = group.Key,
                ResourceUri = packageUriFactory(group.Key),
                DeploymentCount = group.Count()
            })
            .ToList();

        var family = packageKind == DeploymentSourceKind.MapPackage
            ? McpTelemetry.ResourceFamily.MapPackages
            : McpTelemetry.ResourceFamily.AppPackages;
        McpLog.PromotionListRead(_logger, family, items.Count, truncated);

        var view = new McpPackageListView
        {
            ResourceUri = uri,
            PackageKind = packageKindTag,
            Count = items.Count,
            Truncated = truncated,
            Items = items
        };
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPackageListView);
    }
}
