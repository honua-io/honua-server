// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed class InMemoryWorkflowPackageStore : IWorkflowPackageStore
{
    private readonly ConcurrentDictionary<string, WorkflowPackage> _packages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<WorkflowPackageVersion>> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkflowPublication> _publications = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WorkflowPackage> packages = _packages.Values
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(packages);
    }

    public Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();
        _packages.TryGetValue(packageId, out var package);
        return Task.FromResult(package);
    }

    public Task<WorkflowPackage> SavePackageAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var latest = _versions.TryGetValue(package.PackageId, out var versions) && versions.Count > 0
                ? versions.Max(version => version.Version)
                : package.LatestVersion;
            var stored = package with { LatestVersion = latest };
            _packages[package.PackageId] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
        WorkflowPackageValidationResult validation,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageHash);
        ArgumentNullException.ThrowIfNull(validation);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_packages.TryGetValue(packageId, out var package))
            {
                throw new KeyNotFoundException($"Workflow package '{packageId}' was not found.");
            }

            var versions = _versions.GetOrAdd(packageId, _ => []);
            var nextVersion = versions.Count == 0 ? 1 : versions.Max(version => version.Version) + 1;
            var version = new WorkflowPackageVersion
            {
                PackageId = packageId,
                Version = nextVersion,
                SchemaVersion = package.Graph.SchemaVersion,
                PackageHash = packageHash,
                Graph = package.Graph,
                Validation = validation,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = createdBy
            };

            versions.Add(version);
            _packages[packageId] = package with
            {
                LatestVersion = nextVersion,
                UpdatedAt = package.UpdatedAt
            };

            return Task.FromResult(version);
        }
    }

    public Task<WorkflowPackageVersion?> GetVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();

        WorkflowPackageVersion? result;
        lock (_gate)
        {
            result = _versions.TryGetValue(packageId, out var versions)
                ? versions.FirstOrDefault(item => item.Version == version)
                : null;
        }

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<WorkflowPackageVersion> result;
        lock (_gate)
        {
            result = _versions.TryGetValue(packageId, out var versions)
                ? versions.OrderByDescending(version => version.Version).ToArray()
                : Array.Empty<WorkflowPackageVersion>();
        }

        return Task.FromResult(result);
    }

    public Task<WorkflowPublication> SavePublicationAsync(
        WorkflowPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();
        _publications[publication.PublicationId] = publication;
        return Task.FromResult(publication);
    }

    public Task<WorkflowPublication?> GetPublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        cancellationToken.ThrowIfCancellationRequested();
        _publications.TryGetValue(publicationId, out var publication);
        return Task.FromResult(publication);
    }

    public Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WorkflowPublication> result = _publications.Values
            .Where(publication => string.IsNullOrWhiteSpace(packageId)
                                  || string.Equals(publication.PackageId, packageId, StringComparison.Ordinal))
            .OrderByDescending(publication => publication.CreatedAt)
            .ToArray();
        return Task.FromResult(result);
    }
}
