// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataReleasePackageSummaryFactory
{
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
