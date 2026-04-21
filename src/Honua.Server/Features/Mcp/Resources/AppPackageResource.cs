// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://app-packages/{packageId}</c>. App packages do
/// not have a standalone repository on the server — their server-side footprint
/// is the set of deployments that reference the package. The resource therefore
/// reverse-looks-up deployments by
/// <see cref="DeploymentSourceKind.AppPackage"/>. Returns
/// <see cref="GeoprocessingNotFoundException"/> when no deployment references
/// the package so the server never fabricates a surface it cannot reach.
/// </summary>
internal sealed class AppPackageResource : IMcpResource
{
    public const string Template = McpResourceUris.AppPackagesPrefix + "{packageId}";
    private const string PackageKind = "app_package";

    private readonly IDeploymentStore _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<AppPackageResource> _logger;

    public AppPackageResource(
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<AppPackageResource> logger)
    {
        _deployments = deployments;
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
            Description = "App package surface derived from deployments that reference the package. Exposes deployment provenance edges only.",
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
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.Package, OperatorOperation.Read);

        var packageId = uri[McpResourceUris.AppPackagesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.AppPackage, packageId, cancellationToken)
            .ConfigureAwait(false);
        deployments = PackageViewFactory.FilterPublished(deployments);

        if (deployments.Count == 0)
        {
            throw new GeoprocessingNotFoundException($"App package '{packageId}' is not referenced by any currently-published deployment.");
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
