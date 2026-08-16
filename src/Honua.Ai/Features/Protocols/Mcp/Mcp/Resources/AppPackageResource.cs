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
/// MCP resource for <c>honua://app-packages/{packageId}</c>. An app package is
/// reachable in two lifecycle states, and the resource reads both: as a
/// <b>published</b> package, whose server-side footprint is the set of
/// currently-serving deployments that reference it (reverse-looked-up by
/// <see cref="DeploymentSourceKind.AppPackage"/>), and as a <b>draft</b> just
/// created by <c>honua_create_app_package</c>, which has no deployment yet but
/// whose identifier ADR-0076 promises is addressable at this URI
/// (honua-server#3262). Returns <see cref="GeoprocessingNotFoundException"/>
/// when neither knows the package, so the server never fabricates a surface it
/// cannot reach.
/// </summary>
internal sealed class AppPackageResource : IMcpResource
{
    public const string Template = McpResourceUris.AppPackagesPrefix + "{packageId}";
    private const string PackageKind = "app_package";

    private readonly IDeploymentStore _deployments;
    private readonly IPackageDraftStore _drafts;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<AppPackageResource> _logger;

    public AppPackageResource(
        IDeploymentStore deployments,
        IPackageDraftStore drafts,
        IGeoprocessingJobService jobService,
        ILogger<AppPackageResource> logger)
    {
        _deployments = deployments;
        _drafts = drafts;
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.AppPackages;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "App package",
            Description = "App package surface: a created draft held by the draft store, or a package derived from the deployments that reference it. Exposes lifecycle status and deployment provenance edges only.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.AppPackagesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.AppPackagesPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetAppPackage");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.Package, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var packageId = uri[McpResourceUris.AppPackagesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.AppPackage, packageId, cancellationToken)
            .ConfigureAwait(false);
        deployments = PackageViewFactory.FilterPublished(deployments);

        if (deployments.Count == 0)
        {
            // Deployment-backed visibility is checked first so a promoted package
            // keeps reporting its deployment edges rather than the stale draft it
            // grew from. Only when nothing serves it do we fall back to the draft.
            var draft = await _drafts.GetAppDraftAsync(packageId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                throw new GeoprocessingNotFoundException(
                    $"App package '{packageId}' is not a known draft and is not referenced by any currently-published deployment.");
            }

            McpLog.PackageRead(_logger, PackageKind, packageId, 0);

            var draftView = PackageViewFactory.BuildDraft(
                PackageKind,
                packageId,
                McpResourceUris.AppPackageUri(packageId));
            return McpResourceHelpers.SingleJsonContent(uri, draftView, McpJsonContext.Default.McpPackageView);
        }

        McpLog.PackageRead(_logger, PackageKind, packageId, deployments.Count);

        var view = PackageViewFactory.Build(
            PackageKind,
            packageId,
            McpResourceUris.AppPackageUri(packageId),
            deployments);
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPackageView);
    }
}
