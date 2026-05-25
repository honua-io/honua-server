// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Services;

internal sealed class InMemoryMetadataReleasePackageStore : IMetadataReleasePackageStore
{
    private readonly ConcurrentDictionary<Guid, MetadataReleasePackage> _packages = new();
    private readonly ConcurrentDictionary<PackageIdentity, Guid> _packageKeys = new();

    public Task<MetadataReleasePackage> CreateAsync(
        MetadataReleasePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = PackageIdentity.From(package.Metadata);
        if (!_packageKeys.TryAdd(identity, package.PackageId))
        {
            throw new InvalidOperationException(
                $"Metadata release package key '{identity.PackageKey}' already exists in namespace '{identity.Namespace}'.");
        }

        if (!_packages.TryAdd(package.PackageId, package))
        {
            _packageKeys.TryRemove(identity, out _);
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

    private readonly record struct PackageIdentity(string Namespace, string PackageKey)
    {
        public static PackageIdentity From(MetadataV2ObjectMetadata metadata)
        {
            var packageKey = string.IsNullOrWhiteSpace(metadata.Name)
                ? throw new ArgumentException("Package metadata name is required.", nameof(metadata))
                : metadata.Name.Trim();
            var packageNamespace = string.IsNullOrWhiteSpace(metadata.Namespace)
                ? string.Empty
                : metadata.Namespace.Trim();

            return new PackageIdentity(packageNamespace, packageKey);
        }
    }
}
