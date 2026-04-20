// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Reference to the promoted publish or package artifact backing a deployment.
/// Deployments link to canonical publish and package identifiers rather than redefining
/// their semantics; mutation of the underlying artifact remains owned by its lifecycle.
/// </summary>
public sealed record DeploymentSource
{
    /// <summary>
    /// Category of the backing artifact.
    /// </summary>
    public required DeploymentSourceKind Kind { get; init; }

    /// <summary>
    /// Primary identifier of the backing artifact. Matches the kind-specific identifier
    /// (e.g. published service ID, map package ID, or app package ID).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Published service identifier when <see cref="Kind"/> is
    /// <see cref="DeploymentSourceKind.PublishedService"/>, otherwise the published
    /// service that exposes the backing data when applicable.
    /// </summary>
    public string? PublishedServiceId { get; init; }

    /// <summary>
    /// Map package identifier when <see cref="Kind"/> is
    /// <see cref="DeploymentSourceKind.MapPackage"/>, or the map package referenced by
    /// an app package when applicable.
    /// </summary>
    public string? MapPackageId { get; init; }

    /// <summary>
    /// App package identifier when <see cref="Kind"/> is <see cref="DeploymentSourceKind.AppPackage"/>.
    /// </summary>
    public string? AppPackageId { get; init; }

    /// <summary>
    /// Creates a deployment source backed by a published service.
    /// </summary>
    public static DeploymentSource FromPublishedService(string publishedServiceId)
        => new()
        {
            Kind = DeploymentSourceKind.PublishedService,
            SourceId = publishedServiceId,
            PublishedServiceId = publishedServiceId
        };

    /// <summary>
    /// Creates a deployment source backed by a map package.
    /// </summary>
    public static DeploymentSource FromMapPackage(string mapPackageId)
        => new()
        {
            Kind = DeploymentSourceKind.MapPackage,
            SourceId = mapPackageId,
            MapPackageId = mapPackageId
        };

    /// <summary>
    /// Creates a deployment source backed by an app package, optionally referencing the
    /// composed map package and data-backing published service it consumes.
    /// </summary>
    public static DeploymentSource FromAppPackage(
        string appPackageId,
        string? mapPackageId = null,
        string? publishedServiceId = null)
        => new()
        {
            Kind = DeploymentSourceKind.AppPackage,
            SourceId = appPackageId,
            AppPackageId = appPackageId,
            MapPackageId = mapPackageId,
            PublishedServiceId = publishedServiceId
        };
}
