// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Deployment.Domain;

public sealed record DeploymentTarget
{
    public required string TargetId { get; init; }

    public required DeploymentKind Kind { get; init; }

    public required HostingMode HostingMode { get; init; }

    public string? RoutePrefix { get; init; }

    public string? Environment { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

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
