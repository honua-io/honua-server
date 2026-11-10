// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Deployment.Domain;

public sealed record DeploymentSource
{
    public required DeploymentSourceKind Kind { get; init; }

    public required string SourceId { get; init; }

    public string? PublishedServiceId { get; init; }

    public string? MapPackageId { get; init; }

    public string? AppPackageId { get; init; }

    public Guid? StudioItemId { get; init; }

    public Guid? StudioVersionId { get; init; }

    public string? StudioContentHash { get; init; }

    public string? StudioOwnerId { get; init; }

    public Guid? StudioPublicationRequestId { get; init; }

    public Guid? StudioRollbackRequestId { get; init; }

    public StudioPackageFamily? StudioFamily { get; init; }

    public static DeploymentSource FromPublishedService(string publishedServiceId)
        => new()
        {
            Kind = DeploymentSourceKind.PublishedService,
            SourceId = publishedServiceId,
            PublishedServiceId = publishedServiceId
        };

    public static DeploymentSource FromMapPackage(string mapPackageId)
        => new()
        {
            Kind = DeploymentSourceKind.MapPackage,
            SourceId = mapPackageId,
            MapPackageId = mapPackageId
        };

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
