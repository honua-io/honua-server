// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Category of the promoted artifact that backs a deployment. Deployments consume
/// promoted publish and package outputs rather than redefining their semantics.
/// </summary>
public enum DeploymentSourceKind
{
    /// <summary>
    /// A <see cref="Publishing.Domain.PublishedServiceRecord"/> promoted from a publish intent.
    /// </summary>
    [JsonStringEnumMemberName("published_service")]
    PublishedService = 0,

    /// <summary>
    /// A <see cref="Geoprocessing.Domain.MapPackage"/> promoted from a packaging workflow.
    /// </summary>
    [JsonStringEnumMemberName("map_package")]
    MapPackage = 1,

    /// <summary>
    /// A <see cref="Geoprocessing.Domain.AppPackage"/> promoted from a packaging workflow.
    /// </summary>
    [JsonStringEnumMemberName("app_package")]
    AppPackage = 2
}

/// <summary>
/// Shape of the hosted surface exposed by a deployment.
/// </summary>
public enum DeploymentKind
{
    /// <summary>
    /// A hosted feature service surface.
    /// </summary>
    [JsonStringEnumMemberName("feature_service")]
    FeatureService = 0,

    /// <summary>
    /// A hosted map service surface.
    /// </summary>
    [JsonStringEnumMemberName("map_service")]
    MapService = 1,

    /// <summary>
    /// A hosted tile service surface.
    /// </summary>
    [JsonStringEnumMemberName("tile_service")]
    TileService = 2,

    /// <summary>
    /// A static web application or site surface produced from an app package.
    /// </summary>
    [JsonStringEnumMemberName("static_site")]
    StaticSite = 3,

    /// <summary>
    /// An application embedded inside a host shell, exposed under a route prefix.
    /// </summary>
    [JsonStringEnumMemberName("embedded_app")]
    EmbeddedApp = 4
}

/// <summary>
/// Runtime hosting strategy for a deployment.
/// </summary>
public enum HostingMode
{
    /// <summary>
    /// Server-managed hosting for dynamic service endpoints.
    /// </summary>
    [JsonStringEnumMemberName("managed_service")]
    ManagedService = 0,

    /// <summary>
    /// Static asset hosting for application packages.
    /// </summary>
    [JsonStringEnumMemberName("static_site")]
    StaticSite = 1,

    /// <summary>
    /// Embedded hosting inside a host application shell.
    /// </summary>
    [JsonStringEnumMemberName("embedded")]
    Embedded = 2
}

/// <summary>
/// Lifecycle status of a deployment.
/// </summary>
public enum DeploymentStatus
{
    /// <summary>
    /// Deployment created but not yet scheduled or provisioned.
    /// </summary>
    Draft,

    /// <summary>
    /// Deployment is scheduled for a future publication time.
    /// </summary>
    Scheduled,

    /// <summary>
    /// Deployment is allocating or preparing hosting resources.
    /// </summary>
    Provisioning,

    /// <summary>
    /// Deployment is progressing through a rollout strategy.
    /// </summary>
    RollingOut,

    /// <summary>
    /// Deployment is live, serving the promoted artifact.
    /// </summary>
    Active,

    /// <summary>
    /// Deployment has been replaced by a successor deployment at the same target.
    /// </summary>
    Superseded,

    /// <summary>
    /// Deployment was retired and is no longer serving content.
    /// </summary>
    Retired,

    /// <summary>
    /// Deployment failed to reach the active state and requires intervention.
    /// </summary>
    Failed,

    /// <summary>
    /// Deployment was cancelled before reaching a terminal state.
    /// </summary>
    Cancelled
}

/// <summary>
/// Operator-facing publication state used by eval and automation surfaces to observe
/// whether a deployment is currently visible on its routable surface. Aligned with the
/// deployment stage result vocabulary from the deterministic operator workflow contract.
/// </summary>
public enum DeploymentPublicationState
{
    /// <summary>
    /// Deployment exists but is not yet intended for publication.
    /// </summary>
    [JsonStringEnumMemberName("draft")]
    Draft = 0,

    /// <summary>
    /// Deployment is scheduled for a future publication time.
    /// </summary>
    [JsonStringEnumMemberName("scheduled")]
    Scheduled = 1,

    /// <summary>
    /// Deployment is currently published and publicly routable.
    /// </summary>
    [JsonStringEnumMemberName("published")]
    Published = 2,

    /// <summary>
    /// Deployment was previously published and is now intentionally unpublished.
    /// </summary>
    [JsonStringEnumMemberName("unpublished")]
    Unpublished = 3,

    /// <summary>
    /// Deployment has been permanently retired and will not be republished.
    /// </summary>
    [JsonStringEnumMemberName("retired")]
    Retired = 4
}

/// <summary>
/// Rollout strategy describing how a deployment reaches its final serving state.
/// </summary>
public enum RolloutStrategy
{
    /// <summary>
    /// Switch traffic to the new deployment in a single step.
    /// </summary>
    Immediate,

    /// <summary>
    /// Stage the new deployment alongside the current one and cut over as a single step.
    /// </summary>
    BlueGreen,

    /// <summary>
    /// Progressively shift traffic through a sequence of weight steps.
    /// </summary>
    Canary,

    /// <summary>
    /// Rollout is gated by a publication schedule rather than an automatic cutover.
    /// </summary>
    Scheduled
}

/// <summary>
/// Observed state of a rollout within a deployment.
/// </summary>
public enum RolloutState
{
    /// <summary>
    /// Rollout has not yet begun.
    /// </summary>
    Pending,

    /// <summary>
    /// Rollout is actively progressing through steps or a cutover.
    /// </summary>
    InProgress,

    /// <summary>
    /// Rollout has reached its final serving weight.
    /// </summary>
    Promoted,

    /// <summary>
    /// Rollout was reversed to the previous deployment.
    /// </summary>
    RolledBack,

    /// <summary>
    /// Rollout failed before reaching the final serving weight.
    /// </summary>
    Failed,

    /// <summary>
    /// Rollout was cancelled before completion.
    /// </summary>
    Cancelled
}

/// <summary>
/// Observed runtime health of a deployment's serving surface.
/// </summary>
public enum RuntimeHealth
{
    /// <summary>
    /// Health has not been observed yet.
    /// </summary>
    Unknown,

    /// <summary>
    /// Deployment is serving normally.
    /// </summary>
    Healthy,

    /// <summary>
    /// Deployment is serving with reduced capability.
    /// </summary>
    Degraded,

    /// <summary>
    /// Deployment is not serving.
    /// </summary>
    Unhealthy
}
