// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

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

    /// <summary>Studio content item that owns a published immutable version.</summary>
    public Guid? StudioItemId { get; init; }

    /// <summary>Exact immutable Studio version published at the target route.</summary>
    public Guid? StudioVersionId { get; init; }

    /// <summary>SHA-256 content hash of that exact Studio version.</summary>
    public string? StudioContentHash { get; init; }

    /// <summary>Owner of the immutable Studio item at publication time.</summary>
    public string? StudioOwnerId { get; init; }

    /// <summary>Accepted request that moved the serving pointer to this version.</summary>
    public Guid? StudioPublicationRequestId { get; init; }

    /// <summary>Rollback request that restored this version, when applicable.</summary>
    public Guid? StudioRollbackRequestId { get; init; }

    /// <summary>Map or app family of the Studio version.</summary>
    public StudioPackageFamily? StudioFamily { get; init; }

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

    /// <summary>
    /// Binds an accepted Studio map/app publication to its exact immutable version. Studio
    /// item and version identifiers are not treated as geoprocessing package identifiers.
    /// </summary>
    public static DeploymentSource FromStudioPublishedVersion(
        Guid itemId,
        Guid versionId,
        string contentHash,
        string? ownerId,
        Guid publicationRequestId,
        StudioPackageFamily family,
        Guid? rollbackRequestId = null)
    {
        if (family is not (StudioPackageFamily.Map or StudioPackageFamily.App))
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, "Only Studio map and app versions are hosted by this deployment source.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return new DeploymentSource
        {
            Kind = DeploymentSourceKind.StudioPublishedVersion,
            SourceId = versionId.ToString("D"),
            StudioItemId = itemId,
            StudioVersionId = versionId,
            StudioContentHash = contentHash,
            StudioOwnerId = ownerId,
            StudioPublicationRequestId = publicationRequestId,
            StudioRollbackRequestId = rollbackRequestId,
            StudioFamily = family
        };
    }
}
