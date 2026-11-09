// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

public interface IMetadataReleasePackageStore
{
    Task<MetadataReleasePackage> CreateAsync(
        MetadataReleasePackage package,
        CancellationToken cancellationToken = default);

    Task<MetadataReleasePackage?> GetAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetadataReleasePackageSummary>> ListAsync(
        MetadataReleasePackageListFilter filter,
        CancellationToken cancellationToken = default);
}
