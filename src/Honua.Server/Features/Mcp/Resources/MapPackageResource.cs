// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://map-packages/{packageId}</c>. Map packages do
/// not have a standalone repository on the server — their server-side footprint
/// is the set of deployments that reference the package. The resource therefore
/// reverse-looks-up deployments by
/// <see cref="DeploymentSourceKind.MapPackage"/> and surfaces the package as
/// visible whenever at least one deployment references it. When no deployment
/// references the package the resource returns
/// <see cref="GeoprocessingNotFoundException"/>, matching the server's actual
/// knowledge of the package.
/// </summary>
internal sealed class MapPackageResource : IMcpResource
{
    public const string Template = McpResourceUris.MapPackagesPrefix + "{packageId}";
    private const string PackageKind = "map_package";

    private readonly IDeploymentStore _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<MapPackageResource> _logger;

    public MapPackageResource(
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<MapPackageResource> logger)
    {
        _deployments = deployments;
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
            Description = "Map package surface derived from deployments that reference the package. Exposes deployment provenance edges only.",
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
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.Package, OperatorOperation.Read);

        var packageId = uri[McpResourceUris.MapPackagesPrefix.Length..];
        var deployments = await _deployments
            .ListBySourceAsync(DeploymentSourceKind.MapPackage, packageId, cancellationToken)
            .ConfigureAwait(false);

        if (deployments.Count == 0)
        {
            throw new GeoprocessingNotFoundException($"Map package '{packageId}' is not referenced by any deployment.");
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
