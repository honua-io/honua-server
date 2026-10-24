// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Hosting target for a deployment, describing what kind of surface is exposed and where
/// it appears. Deployment targets are independent of the promoted artifact so the same
/// artifact can be deployed to multiple targets (e.g. staging and production).
/// </summary>
public sealed record DeploymentTarget
{
    /// <summary>
    /// Stable identifier for this deployment target.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Category of the hosted surface exposed by this target.
    /// </summary>
    public required DeploymentKind Kind { get; init; }

    /// <summary>
    /// Runtime hosting strategy for this target.
    /// </summary>
    public required HostingMode HostingMode { get; init; }

    /// <summary>
    /// Route prefix under which the deployment is exposed, when applicable.
    /// </summary>
    public string? RoutePrefix { get; init; }

    /// <summary>
    /// Logical environment or stage identifier (e.g. "production", "staging").
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Free-form labels used for filtering, policy, or observability routing.
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Identifies the existing governed Studio route that the publication commits.</summary>
    public static DeploymentTarget ForStudioPublishedRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var key = route[0] == '/' ? route : "/" + route;
        return new DeploymentTarget
        {
            TargetId = "studio-route:" + key,
            Kind = DeploymentKind.StudioPublishedRoute,
            HostingMode = HostingMode.ManagedService,
            RoutePrefix = StudioPublishedRoutes.BuildActiveUrl(key)
        };
    }
}
