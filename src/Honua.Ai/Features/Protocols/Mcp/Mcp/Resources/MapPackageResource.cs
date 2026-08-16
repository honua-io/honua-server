// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://map-packages/{packageId}</c>. A map package is
/// reachable in two lifecycle states, and the resource reads both:
/// <list type="number">
/// <item><description>
/// <b>Published</b> — the package's server-side footprint is the set of
/// deployments that reference it, reverse-looked-up by
/// <see cref="DeploymentSourceKind.MapPackage"/> and visible whenever at least
/// one currently-serving deployment references it.
/// </description></item>
/// <item><description>
/// <b>Draft</b> — a package just created by <c>honua_create_map_package</c> has
/// no deployment yet, but ADR-0076 promises the identifier that tool returns is
/// addressable at this URI. The draft store is therefore consulted when the
/// deployment reverse-lookup finds nothing (honua-server#3262); without it the
/// tool handed back a well-formed URI that could never resolve.
/// </description></item>
/// </list>
/// When neither knows the package the resource returns
/// <see cref="GeoprocessingNotFoundException"/>, matching the server's actual
/// knowledge of the package.
/// </summary>
internal sealed class MapPackageResource : IMcpResource
{
    public const string Template = McpResourceUris.MapPackagesPrefix + "{packageId}";
    private const string PackageKind = "map_package";

    private readonly IDeploymentStore _deployments;
    private readonly IPackageDraftStore _drafts;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<MapPackageResource> _logger;

    public MapPackageResource(
        IDeploymentStore deployments,
        IPackageDraftStore drafts,
        IGeoprocessingJobService jobService,
        ILogger<MapPackageResource> logger)
    {
        _deployments = deployments;
        _drafts = drafts;
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.MapPackages;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Map package",
            Description = "Map package surface: a created draft held by the draft store, or a package derived from the deployments that reference it. Exposes lifecycle status and deployment provenance edges only.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.MapPackagesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.MapPackagesPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetMapPackage");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.Package, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var packageId = uri[McpResourceUris.MapPackagesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.MapPackage, packageId, cancellationToken)
            .ConfigureAwait(false);
        deployments = PackageViewFactory.FilterPublished(deployments);

        if (deployments.Count == 0)
        {
            // Deployment-backed visibility is checked first so a promoted package
            // keeps reporting its deployment edges rather than the stale draft it
            // grew from. Only when nothing serves it do we fall back to the draft.
            var draft = await _drafts.GetMapDraftAsync(packageId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                throw new GeoprocessingNotFoundException(
                    $"Map package '{packageId}' is not a known draft and is not referenced by any currently-published deployment.");
            }

            McpLog.PackageRead(_logger, PackageKind, packageId, 0);

            var draftView = PackageViewFactory.BuildDraft(
                PackageKind,
                packageId,
                McpResourceUris.MapPackageUri(packageId));
            return McpResourceHelpers.SingleJsonContent(uri, draftView, McpJsonContext.Default.McpPackageView);
        }

        McpLog.PackageRead(_logger, PackageKind, packageId, deployments.Count);

        var view = PackageViewFactory.Build(
            PackageKind,
            packageId,
            McpResourceUris.MapPackageUri(packageId),
            deployments);
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPackageView);
    }
}
