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

    public Task<IReadOnlyList<MetadataReleasePackageSummary>> ListAsync(
        MetadataReleasePackageListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var query = _packages.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.SourceEnvironment))
        {
            query = query.Where(package => string.Equals(
                package.SourceEnvironment,
                filter.SourceEnvironment.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.TargetEnvironment))
        {
            query = query.Where(package => package.TargetEnvironments.Any(target => string.Equals(
                target,
                filter.TargetEnvironment.Trim(),
                StringComparison.OrdinalIgnoreCase)));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(package => package.Status == status);
        }

        var summaries = query
            .OrderByDescending(package => package.CreatedAt)
            .ThenByDescending(package => package.PackageId)
            .Skip(Math.Max(0, filter.Offset))
            .Take(Math.Max(0, filter.Limit))
            .Select(MetadataReleasePackageSummaryFactory.From)
            .ToArray();

        return Task.FromResult<IReadOnlyList<MetadataReleasePackageSummary>>(summaries);
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
