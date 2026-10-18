// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>
/// Process-local <see cref="IWorkflowPackageStore"/> for tests and for hosts that
/// do not compose PostGIS (the ephemeral profile). Packages, versions, and
/// publications in this store do not survive a restart and are not visible to a
/// peer process. A schedule publication's compiled <c>WorkflowDefinition</c> still
/// lives in the durable definition store, so after a restart the scheduler can
/// keep firing a definition whose owning publication is gone. That warning applies
/// only to this ephemeral profile. PostGIS hosts use
/// <c>PostgresWorkflowPackageStore</c>, and deleting or disabling the publication
/// removes or disables that definition while this process is still up.
/// </summary>
internal sealed class InMemoryWorkflowPackageStore : IWorkflowPackageStore, IDisposable
{
    private readonly ConcurrentDictionary<string, WorkflowPackage> _packages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<WorkflowPackageVersion>> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkflowPublication> _publications = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly bool _ownsPublicationLock;
    private readonly PublicationLockOwner? _publicationOwner;

    // Serialized mutation views borrow the original store's semaphore. Only the
    // container-owned root store releases it at host shutdown.
    public void Dispose()
    {
        if (!_ownsPublicationLock) _publicationGate.Dispose();
    }

    public InMemoryWorkflowPackageStore() { }
    private InMemoryWorkflowPackageStore(InMemoryWorkflowPackageStore owner)
    {
        _packages = owner._packages; _versions = owner._versions; _publications = owner._publications;
        _gate = owner._gate; _publicationGate = owner._publicationGate; _ownsPublicationLock = true; _publicationOwner = new();
    }

    public async Task<TResult> ExecuteSerializedPublicationMutationAsync<TResult>(
        Func<IWorkflowPackageStore, CancellationToken, Task<TResult>> mutation,
        Func<IWorkflowPackageStore, Exception, CancellationToken, Task> reconcile,
        CancellationToken cancellationToken = default)
        => await ExecuteSerializedPublicationMutationAsync(mutation, reconcile, null, cancellationToken).ConfigureAwait(false);

    public async Task<TResult> ExecuteSerializedPublicationMutationAsync<TResult>(
        Func<IWorkflowPackageStore, CancellationToken, Task<TResult>> mutation,
        Func<IWorkflowPackageStore, Exception, CancellationToken, Task> reconcile,
        Func<IWorkflowPackageStore, CancellationToken, Task>? afterCommit,
        CancellationToken cancellationToken = default)
    {
        if (_ownsPublicationLock)
        {
            _publicationOwner!.RequireActive();
            if (afterCommit is not null) throw new InvalidOperationException("Post-commit activation belongs to the outer publication transaction.");
            return await mutation(this, cancellationToken).ConfigureAwait(false);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        await _publicationGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        var locked = new InMemoryWorkflowPackageStore(this);
        try
        {
            var result = await mutation(locked, bounded.Token).ConfigureAwait(false);
            if (afterCommit is not null) await afterCommit(locked, bounded.Token).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await reconcile(locked, exception, recovery.Token).ConfigureAwait(false);
            throw;
        }
        finally { locked._publicationOwner!.Active = false; _publicationGate.Release(); }
    }


    private sealed class PublicationLockOwner
    {
        public bool Active { get; set; } = true;
        public void RequireActive()
        {
            if (!Active) throw new InvalidOperationException("The publication mutation callback has ended.");
        }
    }

    public Task<WorkflowPackageVersion> CreateInitialVersionOnceAsync(WorkflowPackage package,
        WorkflowPackageValidationResult validation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(validation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var hash = WorkflowPackageGraphHasher.Compute(package.Graph);
            if (!validation.IsValid || validation.PackageHash != hash)
                throw new WorkflowPackageGraphConflictException(package.PackageId);
            var existing = _versions.GetValueOrDefault(package.PackageId)?.SingleOrDefault(version => version.Version == 1);
            if (existing is not null)
            {
                WorkflowPackageContent.RequireInitialVersion(package, existing);
                return Task.FromResult(existing);
            }
            if (_packages.ContainsKey(package.PackageId)) throw new WorkflowPackageGraphConflictException(package.PackageId);
            var created = new WorkflowPackageVersion
            {
                PackageId = package.PackageId,
                Version = 1,
                PackageHash = hash,
                Graph = package.Graph,
                SchemaVersion = package.Graph.SchemaVersion,
                Validation = validation,
                Ownership = package.Ownership,
                CreatedBy = package.CreatedBy,
                CreatedAt = package.CreatedAt,
            };
            _packages[package.PackageId] = package with { LatestVersion = 1 };
            _versions[package.PackageId] = [created];
            return Task.FromResult(created);
        }
    }

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
            // Preserve the first persisted ownership, including unknown legacy provenance.
            var ownership = _packages.TryGetValue(package.PackageId, out var existing)
                ? existing.Ownership
                : package.Ownership;
            var stored = package with { LatestVersion = latest, Ownership = ownership };
            _packages[package.PackageId] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
        WorkflowGraph validatedGraph,
        WorkflowPackageValidationResult validation,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageHash);
        ArgumentNullException.ThrowIfNull(validatedGraph);
        ArgumentNullException.ThrowIfNull(validation);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_packages.TryGetValue(packageId, out var package))
            {
                throw new KeyNotFoundException($"Workflow package '{packageId}' was not found.");
            }

            var lockedHash = WorkflowPackageGraphHasher.Compute(package.Graph);
            var validatedHash = WorkflowPackageGraphHasher.Compute(validatedGraph);
            if (!string.Equals(lockedHash, packageHash, StringComparison.Ordinal) ||
                !string.Equals(validatedHash, packageHash, StringComparison.Ordinal))
            {
                throw new WorkflowPackageGraphConflictException(packageId);
            }

            var versions = _versions.GetOrAdd(packageId, _ => []);
            var nextVersion = versions.Count == 0 ? 1 : versions.Max(version => version.Version) + 1;
            var version = new WorkflowPackageVersion
            {
                PackageId = packageId,
                Version = nextVersion,
                SchemaVersion = string.IsNullOrWhiteSpace(validatedGraph.SchemaVersion)
                    ? "workflow-package.v1"
                    : validatedGraph.SchemaVersion,
                PackageHash = packageHash,
                Ownership = package.Ownership,
                Graph = validatedGraph,
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
        _publicationOwner?.RequireActive();
        if (!_ownsPublicationLock) return ExecuteSerializedPublicationMutationAsync((locked, token) => locked.SavePublicationAsync(publication, token),
            static (_, _, _) => Task.CompletedTask, cancellationToken);
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();

        // Publications are immutable, stable run targets. Reject re-use of an existing
        // publication id rather than overwriting it, so a second publish cannot silently
        // retarget a live publication to a different package/version.
        if (!_publications.TryAdd(publication.PublicationId, publication))
        {
            throw new WorkflowPublicationConflictException(publication.PublicationId);
        }

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

    public Task<WorkflowPublication?> DeletePublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default)
    {
        _publicationOwner?.RequireActive();
        if (!_ownsPublicationLock) return ExecuteSerializedPublicationMutationAsync((locked, token) => locked.DeletePublicationAsync(publicationId, token),
            static (_, _, _) => Task.CompletedTask, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        cancellationToken.ThrowIfCancellationRequested();
        _publications.TryRemove(publicationId, out var publication);
        return Task.FromResult(publication);
    }

    public Task<WorkflowPublication?> SetPublicationStatusAsync(
        string publicationId,
        WorkflowPublicationStatus status,
        CancellationToken cancellationToken = default)
    {
        _publicationOwner?.RequireActive();
        if (!_ownsPublicationLock) return ExecuteSerializedPublicationMutationAsync((locked, token) => locked.SetPublicationStatusAsync(publicationId, status, token),
            static (_, _, _) => Task.CompletedTask, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_publications.TryGetValue(publicationId, out var existing))
            {
                return Task.FromResult<WorkflowPublication?>(null);
            }

            var updated = existing with { Status = status };
            _publications[publicationId] = updated;
            return Task.FromResult<WorkflowPublication?>(updated);
        }
    }

    public Task<bool> TryReplaceWorkflowDefinitionIdAsync(
        string publicationId,
        string expectedDefinitionId,
        string replacementDefinitionId,
        CancellationToken cancellationToken = default)
    {
        _publicationOwner?.RequireActive();
        if (!_ownsPublicationLock) return ExecuteSerializedPublicationMutationAsync((locked, token) => locked.TryReplaceWorkflowDefinitionIdAsync(publicationId, expectedDefinitionId, replacementDefinitionId, token),
            static (_, _, _) => Task.CompletedTask, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementDefinitionId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_publications.TryGetValue(publicationId, out var existing)
                || !string.Equals(existing.WorkflowDefinitionId, expectedDefinitionId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _publications[publicationId] = existing with { WorkflowDefinitionId = replacementDefinitionId };
            return Task.FromResult(true);
        }
    }
}
