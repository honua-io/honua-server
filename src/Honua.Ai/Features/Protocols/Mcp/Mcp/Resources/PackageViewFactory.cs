// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Deployment.Domain;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// Shared builder for <see cref="McpPackageView"/> payloads returned by
/// <see cref="MapPackageResource"/> and <see cref="AppPackageResource"/>. Both
/// resources derive their view from the same deployment reverse-lookup, so the
/// factory centralizes the published-deployments projection that governs
/// package visibility, package list membership, and the
/// <c>deploymentResourceUris</c> field returned on the published-service
/// detail view.
/// </summary>
internal static class PackageViewFactory
{
    // Packages (and the hosted-deployment list on a published service) are only
    // considered active when a deployment's PublicationState is Published —
    // i.e. the deployment is currently routable. Draft/Scheduled/Provisioning/
    // RollingOut pre-serving states, Unpublished terminal states (Failed,
    // Cancelled, Superseded), and Retired are all excluded so the MCP
    // resources never advertise a package, service, or deployment edge that is
    // not currently serving traffic.
    public static IReadOnlyList<Deployment> FilterPublished(IReadOnlyList<Deployment> deployments)
    {
        var firstExcluded = -1;
        for (var i = 0; i < deployments.Count; i++)
        {
            if (!IsPublished(deployments[i]))
            {
                firstExcluded = i;
                break;
            }
        }

        if (firstExcluded < 0)
        {
            return deployments;
        }

        var published = new List<Deployment>(deployments.Count);
        for (var i = 0; i < firstExcluded; i++)
        {
            published.Add(deployments[i]);
        }
        for (var i = firstExcluded + 1; i < deployments.Count; i++)
        {
            if (IsPublished(deployments[i]))
            {
                published.Add(deployments[i]);
            }
        }

        return published;
    }

    public static bool IsPublished(Deployment deployment) =>
        deployment.PublicationState == DeploymentPublicationState.Published;

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
