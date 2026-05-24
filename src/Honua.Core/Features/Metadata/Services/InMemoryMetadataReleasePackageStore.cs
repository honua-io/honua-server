// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Services;

internal sealed class InMemoryMetadataReleasePackageStore : IMetadataReleasePackageStore
{
    private readonly ConcurrentDictionary<Guid, MetadataReleasePackage> _packages = new();

    public Task<MetadataReleasePackage> CreateAsync(
        MetadataReleasePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_packages.TryAdd(package.PackageId, package))
        {
            throw new InvalidOperationException($"Metadata release package '{package.PackageId}' already exists.");
        }

        return Task.FromResult(package);
    }

    public Task<MetadataReleasePackage?> GetAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _packages.TryGetValue(packageId, out var package);
        return Task.FromResult(package);
    }
}
