// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Deployment.Domain;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Resources;

/// <summary>
/// Shared builder for <see cref="McpPackageView"/> payloads returned by
/// <see cref="MapPackageResource"/> and <see cref="AppPackageResource"/>. Both
/// resources derive their view from the same deployment reverse-lookup, so the
/// factory centralizes the reachable-deployments projection.
/// </summary>
internal static class PackageViewFactory
{
    public static McpPackageView Build(
        string packageKind,
        string packageId,
        string resourceUri,
        IReadOnlyList<Deployment> deployments)
    {
        var deploymentUris = deployments
            .Select(deployment => McpResourceUris.DeploymentUri(deployment.DeploymentId))
            .ToList();

        var parentDeployment = deployments.Count > 0
            ? McpResourceUris.DeploymentUri(deployments[0].DeploymentId)
            : null;

        return new McpPackageView
        {
            PackageKind = packageKind,
            PackageId = packageId,
            ResourceUri = resourceUri,
            DeploymentCount = deployments.Count,
            DeploymentResourceUris = deploymentUris,
            Provenance = new McpHostedProvenance
            {
                ParentDeploymentResourceUri = parentDeployment
            }
        };
    }
}
