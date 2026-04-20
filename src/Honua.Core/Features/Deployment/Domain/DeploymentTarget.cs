// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
