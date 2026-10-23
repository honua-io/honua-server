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
/// MCP resource handling the four list-root URIs that enumerate promotion
/// surfaces without a trailing identifier:
/// <c>honua://published-services</c>, <c>honua://deployments</c>,
/// <c>honua://map-packages</c>, and <c>honua://app-packages</c>. Each returns
/// a capped summary list drawn from canonical stores. Published services also
/// include ordinary Metadata v2 layer publications when they are serving;
/// their detail URI is backed by the same persisted graph. Services and
/// deployments are narrowed past the store's broader "non-decommissioned" /
/// "non-retired-non-superseded" semantics to the truly live subsets — services
/// with <see cref="PublishedServiceStatus.Active"/> (or routable metadata
/// publications) and deployments with
/// <see cref="DeploymentPublicationState.Published"/> — so the advertised
/// "active" descriptors match the wire contract. Package roots reuse the same
/// published-deployment filter from <see cref="PackageViewFactory.IsPublished"/>
/// so map/app packages only appear when at least one deployment is currently
/// serving them. The cap (<see cref="DefaultPageSize"/>) is applied after
/// filtering so agents never download unbounded catalogs; a <c>truncated</c>
/// flag signals when results were clipped.
/// </summary>
internal sealed class PromotionSurfaceIndexResource : IMcpResource
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private readonly IPublishedServiceStore? _services;
    private readonly IDeploymentStore? _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<PromotionSurfaceIndexResource> _logger;
    private readonly int _pageSize;

    public PromotionSurfaceIndexResource(
        IPublishedServiceStore? services,
        IDeploymentStore? deployments,
        IGeoprocessingJobService jobService,
        ILogger<PromotionSurfaceIndexResource> logger)
        : this(services, deployments, jobService, logger, DefaultPageSize)
    {
    }

    internal PromotionSurfaceIndexResource(
        IPublishedServiceStore? services,
        IDeploymentStore? deployments,
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

    // Counter telemetry routes through the per-URI family so the four promotion
    // roots — published-services, deployments, map-packages, app-packages —
    // emit distinct `resource_family` tag values. Without this the dispatcher
    // would collapse all four roots into a single `promotion-index` series and
    // lose per-root success/error observability.
    public string ResolveFamily(string uri)
    {
        if (string.Equals(uri, McpResourceUris.PublishedServicesRoot, StringComparison.Ordinal))
        {
            return McpTelemetry.ResourceFamily.PublishedServices;
        }

        if (string.Equals(uri, McpResourceUris.DeploymentsRoot, StringComparison.Ordinal))
        {
            return McpTelemetry.ResourceFamily.Deployments;
        }

        if (string.Equals(uri, McpResourceUris.MapPackagesRoot, StringComparison.Ordinal))
        {
            return McpTelemetry.ResourceFamily.MapPackages;
        }

        if (string.Equals(uri, McpResourceUris.AppPackagesRoot, StringComparison.Ordinal))
        {
            return McpTelemetry.ResourceFamily.AppPackages;
        }

        return Family;
    }

    public IReadOnlyList<McpResourceDescriptor> Describe() => _deployments is null
        ? [PublishedServicesDescriptor()]
        : AllDescriptors();

    private static McpResourceDescriptor PublishedServicesDescriptor() => new()
    {
        Uri = McpResourceUris.PublishedServicesRoot,
        Name = "Published services index",
        Description = "Published services currently serving requests.",
        MimeType = McpResourceHelpers.JsonMimeType
    };

    private static McpResourceDescriptor[] AllDescriptors() => new[]
    {
        PublishedServicesDescriptor(),
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.DeploymentsRoot,
            Name = "Deployments index",
            Description = "Deployments whose publication state is Published (currently routable).",
            MimeType = McpResourceHelpers.JsonMimeType
        },
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.MapPackagesRoot,
            Name = "Map packages index",
            Description = "Map packages referenced by at least one currently-published deployment.",
            MimeType = McpResourceHelpers.JsonMimeType
        },
        new McpResourceDescriptor
        {
            Uri = McpResourceUris.AppPackagesRoot,
            Name = "App packages index",
            Description = "App packages referenced by at least one currently-published deployment.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) =>
        string.Equals(uri, McpResourceUris.PublishedServicesRoot, StringComparison.Ordinal) ||
        (_deployments is not null &&
            (string.Equals(uri, McpResourceUris.DeploymentsRoot, StringComparison.Ordinal) ||
             string.Equals(uri, McpResourceUris.MapPackagesRoot, StringComparison.Ordinal) ||
             string.Equals(uri, McpResourceUris.AppPackagesRoot, StringComparison.Ordinal)));

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        if (string.Equals(uri, McpResourceUris.PublishedServicesRoot, StringComparison.Ordinal))
        {
            return await ReadPublishedServiceIndexAsync(httpContext, principal, uri, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(uri, McpResourceUris.DeploymentsRoot, StringComparison.Ordinal))
        {
            return await ReadDeploymentIndexAsync(httpContext, principal, uri, cancellationToken).ConfigureAwait(false);
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
        HttpContext httpContext,
        System.Security.Claims.ClaimsPrincipal principal,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListPublishedServices");
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.PublishedService, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);
        McpLog.ResourceRead(_logger, McpTelemetry.ResourceFamily.PublishedServices, uri);

        // ListActiveAsync only excludes decommissioned services, so we narrow to the
        // Active subset here to match the "active published services" descriptor
        // and keep the list root consistent with what agents expect when they ask
        // for the currently-serving catalog.
        IReadOnlyList<PublishedServiceRecord> records = _services is null
            ? []
            : await _services.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var active = new List<PublishedServiceRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Status == PublishedServiceStatus.Active)
            {
                active.Add(records[i]);
            }
        }
        // Impose a deterministic order before truncating so `truncated=true`
        // responses return the same prefix across calls regardless of the
        // backing store's iteration order.
        var activePromotionNames = new HashSet<string>(
            active.Select(record => record.ServiceId), StringComparer.OrdinalIgnoreCase);
        var items = active
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

        var graphProvider = httpContext.RequestServices.GetService<IMetadataV2GraphProvider>();
        if (graphProvider is not null)
        {
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await PublishedMetadataCatalogProjection.ListReadableAsync(
                httpContext, snapshot, cancellationToken).ConfigureAwait(false);
            // Detail reads also reserve the public URI for inactive promotions.
            // Fetch all matching identities in one query rather than doing a
            // store round trip for every catalog service before the page cap.
            var collidingNames = new HashSet<string>(
                _services is null
                    ? []
                    : await _services.FindCollidingPublicIdsAsync(
                        catalog.Select(service => service.ServiceName).ToArray(),
                        cancellationToken).ConfigureAwait(false),
                StringComparer.Ordinal);
            foreach (var service in catalog)
            {
                if (activePromotionNames.Contains(service.ServiceName) || collidingNames.Contains(service.ServiceName))
                {
                    throw new GeoprocessingValidationException(
                        $"Published service identity '{service.ServiceName}' is shared by a promotion and a catalog publication.");
                }

                if (service.Serving)
                {
                    items.Add(service.ToSummary());
                }
            }
        }

        items.Sort(static (a, b) => string.CompareOrdinal(a.ServiceId, b.ServiceId));
        var truncated = items.Count > _pageSize;
        items = items.Take(_pageSize).ToList();

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

    private IDeploymentStore RequireDeploymentStore() => _deployments
        ?? throw new GeoprocessingNotFoundException("Promotion deployment resources are not configured.");

    private async Task<McpResourcesReadResult> ReadDeploymentIndexAsync(
        HttpContext httpContext,
        System.Security.Claims.ClaimsPrincipal principal,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListDeployments");
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.Deployment, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);
        McpLog.ResourceRead(_logger, McpTelemetry.ResourceFamily.Deployments, uri);

        // ListActiveAsync excludes Retired and Superseded but still surfaces
        // Failed, Cancelled, Draft, Scheduled, Provisioning, and RollingOut
        // deployments. Narrow to PublicationState == Published so the list-root
        // contract ("active deployments") matches the set of deployments that
        // are currently routable, consistent with the package visibility filter
        // in PackageViewFactory.
        var records = await RequireDeploymentStore().ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var published = PackageViewFactory.FilterPublished(records);
        var visible = new List<Deployment>();
        foreach (var deployment in published)
        {
            if (await StudioDeploymentReadAuthority.CanReadAsync(
                    httpContext, deployment, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(deployment);
            }
        }
        // Impose a deterministic order before truncating so `truncated=true`
        // responses return the same prefix across calls regardless of the
        // backing store's iteration order.
        var orderedPublished = visible
            .OrderBy(deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .ToList();
        var truncated = orderedPublished.Count > _pageSize;
        var items = orderedPublished
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
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.Package, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);
        var listFamily = packageKind == DeploymentSourceKind.MapPackage
            ? McpTelemetry.ResourceFamily.MapPackages
            : McpTelemetry.ResourceFamily.AppPackages;
        McpLog.ResourceRead(_logger, listFamily, uri);

        // Packages are visible only through currently-published deployments so the
        // list root agrees with the package detail 404 contract — a package that
        // is referenced by no published deployment must not appear in the index.
        var deployments = await RequireDeploymentStore().ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var grouped = deployments
            .Where(deployment =>
                deployment.Source.Kind == packageKind
                && PackageViewFactory.IsPublished(deployment))
            .GroupBy(deployment => deployment.Source.SourceId, StringComparer.Ordinal)
            // Impose a deterministic order before truncating so `truncated=true`
            // responses return the same prefix across calls regardless of the
            // backing store's iteration order.
            .OrderBy(group => group.Key, StringComparer.Ordinal)
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

        McpLog.PromotionListRead(_logger, listFamily, items.Count, truncated);

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
