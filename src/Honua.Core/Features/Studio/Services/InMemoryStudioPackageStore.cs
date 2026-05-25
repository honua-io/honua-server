// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// In-memory Studio package store for tests and local fallback scenarios.
/// </summary>
public sealed class InMemoryStudioPackageStore : IStudioPackageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StudioPackageDraft> _drafts = new();
    private readonly Dictionary<Guid, StudioContentItemState> _items = new();
    private readonly Dictionary<Guid, List<StudioContentVersion>> _versionsByItem = new();
    private readonly Dictionary<Guid, StudioPublicationRequest> _publicationRequests = new();
    private readonly Dictionary<Guid, StudioRollbackRequest> _rollbackRequests = new();

    /// <inheritdoc />
    public StudioPackagePersistenceMode PersistenceMode => StudioPackagePersistenceMode.InMemory;

    /// <inheritdoc />
    public Task<StudioPackageDraft> CreateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            EnsurePackageKeyAvailable(draft);
            var item = GetOrCreateItem(draft.ItemId, draft.PackageKey, draft.WorkspaceId, draft.Family, draft.CreatedBy, draft.CreatedAt);
            _items[draft.ItemId] = item with
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                Family = draft.Family,
                UpdatedBy = draft.UpdatedBy,
                UpdatedAt = draft.UpdatedAt,
            };
            _drafts.Add(draft.DraftId, draft);
            return Task.FromResult(draft);
        }
    }

    /// <inheritdoc />
    public Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _drafts.TryGetValue(draftId, out var draft);
            return Task.FromResult(draft);
        }
    }

    /// <inheritdoc />
    public Task<StudioPackageDraft?> UpdateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_drafts.TryGetValue(draft.DraftId, out var existing))
            {
                return Task.FromResult<StudioPackageDraft?>(null);
            }

            if (draft.Generation != existing.Generation)
            {
                throw new InvalidOperationException("Stale draft generation; refresh and retry.");
            }

            EnsurePackageKeyAvailable(draft);
            var updated = draft with { Generation = existing.Generation + 1 };
            _drafts[draft.DraftId] = updated;
            _items[draft.ItemId] = GetOrCreateItem(draft.ItemId, draft.PackageKey, draft.WorkspaceId, draft.Family, draft.CreatedBy, draft.CreatedAt) with
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                Family = draft.Family,
                UpdatedBy = draft.UpdatedBy,
                UpdatedAt = draft.UpdatedAt,
            };
            return Task.FromResult<StudioPackageDraft?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_drafts.Remove(draftId));
        }
    }

    /// <inheritdoc />
    public Task<StudioContentVersion> CreateVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_drafts.ContainsKey(draft.DraftId))
            {
                throw new KeyNotFoundException("Studio package draft was not found.");
            }

            var item = GetOrCreateItem(draft.ItemId, draft.PackageKey, draft.WorkspaceId, draft.Family, draft.CreatedBy, draft.CreatedAt);
            var versions = GetVersions(draft.ItemId);
            var version = new StudioContentVersion
            {
                ItemId = draft.ItemId,
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                OwnerId = draft.OwnerId,
                VersionId = Guid.NewGuid(),
                VersionNumber = versions.Count == 0 ? 1 : versions[^1].VersionNumber + 1,
                ContentHash = StudioPackageHash.Compute(draft.Envelope),
                Envelope = draft.Envelope,
                Validation = draft.Validation,
                Dependencies = draft.Envelope.Dependencies,
                Provenance = draft.Envelope.Provenance,
                SourceDraftId = draft.DraftId,
                BaseVersionId = draft.BaseVersionId,
                ChangeNote = changeNote,
                CreatedBy = actorId,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            versions.Add(version);
            _items[draft.ItemId] = item with
            {
                CurrentVersionId = version.VersionId,
                UpdatedBy = actorId,
                UpdatedAt = version.CreatedAt,
            };
            return Task.FromResult(version);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StudioContentVersion> versions = GetVersions(itemId).OrderBy(static v => v.VersionNumber).ToArray();
            return Task.FromResult(versions);
        }
    }

    /// <inheritdoc />
    public Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var version = GetVersions(itemId).FirstOrDefault(v => v.VersionId == versionId);
            return Task.FromResult(version);
        }
    }

    /// <inheritdoc />
    public Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(itemId, out var item)
                ? new StudioContentItemPointers
                {
                    ItemId = item.ItemId,
                    CurrentVersionId = item.CurrentVersionId,
                    PublishedVersionId = item.PublishedVersionId,
                }
                : null);
        }
    }

    /// <inheritdoc />
    public Task<StudioPublicationRequest> CreatePublicationRequestAsync(
        StudioPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!StudioPackageEnumHelpers.IsDefined(request.Status))
            {
                throw new ArgumentException("Publication request status is not supported.", nameof(request));
            }

            var version = GetVersions(request.ItemId).FirstOrDefault(v => v.VersionId == request.VersionId);
            if (version is null)
            {
                throw new KeyNotFoundException("Studio content version was not found.");
            }

            _publicationRequests.Add(request.RequestId, request);
            if (request.Status != StudioPublicationRequestStatus.Accepted)
            {
                return Task.FromResult(request);
            }

            var item = GetOrCreateItem(
                request.ItemId,
                version.PackageKey,
                version.WorkspaceId,
                version.Envelope.Family,
                request.RequestedBy,
                request.CreatedAt);
            _items[request.ItemId] = item with
            {
                PublishedVersionId = request.VersionId,
                UpdatedBy = request.RequestedBy,
                UpdatedAt = request.CreatedAt,
            };
            return Task.FromResult(request);
        }
    }

    /// <inheritdoc />
    public Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!StudioPackageEnumHelpers.IsDefined(target))
            {
                throw new ArgumentException("Rollback pointer is not supported.", nameof(target));
            }

            var version = GetVersions(itemId).FirstOrDefault(v => v.VersionId == targetVersionId);
            if (version is null || !_items.TryGetValue(itemId, out var item))
            {
                throw new KeyNotFoundException("Studio content version was not found.");
            }

            var now = DateTimeOffset.UtcNow;
            var updatedItem = item with
            {
                CurrentVersionId = target is StudioRollbackPointer.Current or StudioRollbackPointer.Both ? targetVersionId : item.CurrentVersionId,
                PublishedVersionId = target is StudioRollbackPointer.Published or StudioRollbackPointer.Both ? targetVersionId : item.PublishedVersionId,
                UpdatedBy = actorId,
                UpdatedAt = now,
            };
            _items[itemId] = updatedItem;
            var request = new StudioRollbackRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = itemId,
                TargetVersionId = targetVersionId,
                Target = target,
                Pointers = new StudioContentItemPointers
                {
                    ItemId = itemId,
                    CurrentVersionId = updatedItem.CurrentVersionId,
                    PublishedVersionId = updatedItem.PublishedVersionId,
                },
                RequestedBy = actorId,
                Reason = reason,
                CreatedAt = now,
            };
            _rollbackRequests.Add(request.RequestId, request);
            return Task.FromResult(request);
        }
    }

    private StudioContentItemState GetOrCreateItem(
        Guid itemId,
        string packageKey,
        string? workspaceId,
        StudioPackageFamily family,
        string? actorId,
        DateTimeOffset timestamp)
    {
        if (_items.TryGetValue(itemId, out var item))
        {
            return item;
        }

        return new StudioContentItemState(
            itemId,
            packageKey,
            workspaceId,
            family,
            CurrentVersionId: null,
            PublishedVersionId: null,
            CreatedBy: actorId,
            UpdatedBy: actorId,
            CreatedAt: timestamp,
            UpdatedAt: timestamp);
    }

    private List<StudioContentVersion> GetVersions(Guid itemId)
    {
        if (!_versionsByItem.TryGetValue(itemId, out var versions))
        {
            versions = [];
            _versionsByItem.Add(itemId, versions);
        }

        return versions;
    }

    private void EnsurePackageKeyAvailable(StudioPackageDraft draft)
    {
        foreach (var item in _items.Values)
        {
            if (item.ItemId != draft.ItemId &&
                item.Family == draft.Family &&
                string.Equals(item.PackageKey, draft.PackageKey, StringComparison.Ordinal) &&
                string.Equals(item.WorkspaceId ?? string.Empty, draft.WorkspaceId ?? string.Empty, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Studio package key conflicts with an existing content item.");
            }
        }
    }

    private sealed record StudioContentItemState(
        Guid ItemId,
        string PackageKey,
        string? WorkspaceId,
        StudioPackageFamily Family,
        Guid? CurrentVersionId,
        Guid? PublishedVersionId,
        string? CreatedBy,
        string? UpdatedBy,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
