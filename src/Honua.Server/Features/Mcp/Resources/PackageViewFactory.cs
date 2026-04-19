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
    // Packages are visible only through deployments that are still reachable, matching
    // the active-deployment filter in IDeploymentStore.ListActiveAsync so detail and
    // list-root reads agree on visibility.
    public static IReadOnlyList<Deployment> FilterActive(IReadOnlyList<Deployment> deployments)
    {
        var firstInactive = -1;
        for (var i = 0; i < deployments.Count; i++)
        {
            if (IsInactive(deployments[i]))
            {
                firstInactive = i;
                break;
            }
        }

        if (firstInactive < 0)
        {
            return deployments;
        }

        var active = new List<Deployment>(deployments.Count);
        for (var i = 0; i < firstInactive; i++)
        {
            active.Add(deployments[i]);
        }
        for (var i = firstInactive + 1; i < deployments.Count; i++)
        {
            if (!IsInactive(deployments[i]))
            {
                active.Add(deployments[i]);
            }
        }

        return active;
    }

    private static bool IsInactive(Deployment deployment) =>
        deployment.Status == DeploymentStatus.Retired
        || deployment.Status == DeploymentStatus.Superseded;

    public static McpPackageView Build(
        string packageKind,
        string packageId,
        string resourceUri,
        IReadOnlyList<Deployment> deployments)
    {
        // Sort by deployment id so list-root and detail reads produce stable output
        // regardless of the store's reverse-lookup ordering. Packages have no canonical
        // parent deployment, so DeploymentResourceUris is the canonical access path.
        var deploymentUris = deployments
            .Select(deployment => McpResourceUris.DeploymentUri(deployment.DeploymentId))
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .ToList();

        return new McpPackageView
        {
            PackageKind = packageKind,
            PackageId = packageId,
            ResourceUri = resourceUri,
            DeploymentCount = deployments.Count,
            DeploymentResourceUris = deploymentUris,
            Provenance = new McpHostedProvenance()
        };
    }
}
