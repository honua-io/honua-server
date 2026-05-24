// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Persistence contract for metadata release packages.
/// </summary>
public interface IMetadataReleasePackageStore
{
    /// <summary>
    /// Persists a metadata release package.
    /// </summary>
    Task<MetadataReleasePackage> CreateAsync(
        MetadataReleasePackage package,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a metadata release package by identifier, or null when not found.
    /// </summary>
    Task<MetadataReleasePackage?> GetAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);
}
