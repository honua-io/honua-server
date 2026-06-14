// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Projects a full <see cref="MetadataReleasePackage"/> into its secret-safe
/// <see cref="MetadataReleasePackageSummary"/> for the release-package list surface.
/// </summary>
public static class MetadataReleasePackageSummaryFactory
{
    /// <summary>Projects a persisted package into a list summary.</summary>
    public static MetadataReleasePackageSummary From(MetadataReleasePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new MetadataReleasePackageSummary
        {
            PackageId = package.PackageId,
            PackageKey = package.Metadata.Name,
            Namespace = package.Metadata.Namespace,
            Title = package.Metadata.Title,
            Summary = package.Metadata.Description,
            SourceEnvironment = package.SourceEnvironment,
            SourceRevision = package.SourceRevision,
            TargetEnvironments = package.TargetEnvironments,
            EntryCount = package.Entries.Count,
            Status = package.Status,
            CreatedBy = package.CreatedBy,
            CreatedAt = package.CreatedAt,
            UpdatedAt = package.UpdatedAt,
        };
    }
}
