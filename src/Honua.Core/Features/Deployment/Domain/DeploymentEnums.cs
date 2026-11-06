// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Deployment.Domain;

public enum DeploymentSourceKind
{
    [JsonStringEnumMemberName("published_service")]
    PublishedService = 0,

    [JsonStringEnumMemberName("map_package")]
    MapPackage = 1,

    [JsonStringEnumMemberName("app_package")]
    AppPackage = 2,

    [JsonStringEnumMemberName("studio_published_version")]
    StudioPublishedVersion = 3
}

public enum DeploymentKind
{
    [JsonStringEnumMemberName("feature_service")]
    FeatureService = 0,

    [JsonStringEnumMemberName("map_service")]
    MapService = 1,

    [JsonStringEnumMemberName("tile_service")]
    TileService = 2,

    [JsonStringEnumMemberName("static_site")]
    StaticSite = 3,

    [JsonStringEnumMemberName("embedded_app")]
    EmbeddedApp = 4,

    [JsonStringEnumMemberName("studio_published_route")]
    StudioPublishedRoute = 5
}

public enum HostingMode
{
    [JsonStringEnumMemberName("managed_service")]
    ManagedService = 0,

    [JsonStringEnumMemberName("static_site")]
    StaticSite = 1,

    [JsonStringEnumMemberName("embedded")]
    Embedded = 2
}

public enum DeploymentStatus
{
    Draft,

    Scheduled,

    Provisioning,

    RollingOut,

    Active,

    Superseded,

    Retired,

    Failed,

    Cancelled
}

public enum DeploymentPublicationState
{
    [JsonStringEnumMemberName("draft")]
    Draft = 0,

    [JsonStringEnumMemberName("scheduled")]
    Scheduled = 1,

    [JsonStringEnumMemberName("published")]
    Published = 2,

    [JsonStringEnumMemberName("unpublished")]
    Unpublished = 3,

    [JsonStringEnumMemberName("retired")]
    Retired = 4
}

public enum RolloutStrategy
{
    Immediate,

    BlueGreen,

    Canary,

    Scheduled
}

public enum RolloutState
{
    Pending,

    InProgress,

    Promoted,

    RolledBack,

    Failed,

    Cancelled
}

public enum RuntimeHealth
{
    Unknown,

    Healthy,

    Degraded,

    Unhealthy
}
