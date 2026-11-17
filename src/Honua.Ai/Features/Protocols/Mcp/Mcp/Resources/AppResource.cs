// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

internal sealed class AppResource : IMcpResource
{
    public const string Template = McpResourceUris.AppsPrefix + "{appId}";
    private const string PackageKind = "app";

    private readonly IDeploymentStore _deployments;
    private readonly IPackageDraftStore _drafts;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<AppResource> _logger;

    public AppResource(
        IDeploymentStore deployments,
        IPackageDraftStore drafts,
        IGeoprocessingJobService jobService,
        ILogger<AppResource> logger)
    {
        _deployments = deployments;
        _drafts = drafts;
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Apps;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "App",
            Description = "App surface: a created draft held by the draft store, or a package derived from the deployments that reference it. Exposes lifecycle status and deployment provenance edges only.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.AppsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.AppsPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetApp");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService.EnsureCallerAuthorizedAsync(
                principal, OperatorResourceType.Package, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var appId = uri[McpResourceUris.AppsPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.App, appId, cancellationToken)
            .ConfigureAwait(false);
        deployments = PackageViewFactory.FilterPublished(deployments);

        if (deployments.Count == 0)
        {
            // Deployment-backed visibility is checked first so a promoted package
            // keeps reporting its deployment edges rather than the stale draft it
            // grew from. Only when nothing serves it do we fall back to the draft.
            var ownerId = PackageDraftCaller.ResolveOwnerId(principal);
            var draft = string.IsNullOrWhiteSpace(ownerId)
                ? null
                : await _drafts.GetAppDraftAsync(appId, ownerId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                throw new GeoprocessingNotFoundException(
                    $"App '{appId}' is not a known draft and is not referenced by any currently-published deployment.");
            }

            McpLog.PackageRead(_logger, PackageKind, appId, 0);

            var draftView = PackageViewFactory.BuildDraft(
                PackageKind,
                appId,
                McpResourceUris.AppUri(appId));
            return McpResourceHelpers.SingleJsonContent(uri, draftView, McpJsonContext.Default.McpPackageView);
        }

        McpLog.PackageRead(_logger, PackageKind, appId, deployments.Count);

        var view = PackageViewFactory.Build(
            PackageKind,
            appId,
            McpResourceUris.AppUri(appId),
            deployments);
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpPackageView);
    }
}
