// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// In-memory Studio package store for tests and local fallback scenarios.
/// </summary>
public sealed class InMemoryStudioPackageStore : IStudioPackageStore
{
    private const int DefaultListLimit = 25;
    private const int MaxListLimit = 200;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, StudioPackageDraft> _drafts = new();
    private readonly Dictionary<Guid, StudioContentItemRecord> _items = new();
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
            var item = GetOrCreateItem(draft.ItemId, draft.PackageKey, draft.WorkspaceId, draft.Family, draft.OwnerId, draft.CreatedBy, draft.CreatedAt);
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
            _items[draft.ItemId] = GetOrCreateItem(draft.ItemId, draft.PackageKey, draft.WorkspaceId, draft.Family, draft.OwnerId, draft.CreatedBy, draft.CreatedAt) with
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
            if (!_drafts.TryGetValue(draftId, out var draft))
            {
                return Task.FromResult(false);
            }

            _drafts.Remove(draftId);

            // Cascade-delete an orphan content item: if this was the item's last remaining
            // draft and no version has ever been saved for it, the item has nothing openable
            // (no draft, no version) and must not surface in content-item enumeration (#3003).
            if (_items.TryGetValue(draft.ItemId, out var item) &&
                item.CurrentVersionId is null &&
                item.PublishedVersionId is null &&
                !_drafts.Values.Any(d => d.ItemId == draft.ItemId))
            {
                _items.Remove(draft.ItemId);
                _versionsByItem.Remove(draft.ItemId);
            }

            return Task.FromResult(true);
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

            var item = GetOrCreateItem(draft.ItemId, draft.PackageKey, draft.WorkspaceId, draft.Family, draft.OwnerId, draft.CreatedBy, draft.CreatedAt);
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
                    OwnerId = item.OwnerId,
                }
                : null);
        }
    }

    /// <inheritdoc />
    public Task<StudioContentItemListResult> ListContentItemsAsync(
        StudioContentItemQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var limit = ClampLimit(query.Limit);

        lock (_gate)
        {
            var matched = _items.Values.Where(item => MatchesItemQuery(item, query)).ToList();
            var total = matched.Count;
            IEnumerable<StudioContentItemRecord> ordered = matched
                .OrderByDescending(static item => item.UpdatedAt)
                .ThenByDescending(static item => item.ItemId);

            if (StudioListCursor.TryDecode(query.Cursor, out var cursorUpdatedAt, out var cursorId))
            {
                ordered = ordered.Where(item =>
                    item.UpdatedAt < cursorUpdatedAt
                    || (item.UpdatedAt == cursorUpdatedAt && item.ItemId.CompareTo(cursorId) < 0));
            }

            var page = ordered.Take(limit + 1).ToList();
            string? nextCursor = null;
            if (page.Count > limit)
            {
                var lastKept = page[limit - 1];
                page.RemoveAt(page.Count - 1);
                nextCursor = StudioListCursor.Encode(lastKept.UpdatedAt, lastKept.ItemId);
            }

            IReadOnlyList<StudioContentItemSummary> items = page.Select(ToSummary).ToArray();
            return Task.FromResult(new StudioContentItemListResult
            {
                Items = items,
                Total = total,
                NextCursor = nextCursor,
            });
        }
    }

    /// <inheritdoc />
    public Task<StudioPackageDraftListResult> ListDraftsAsync(
        StudioPackageDraftQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var limit = ClampLimit(query.Limit);

        lock (_gate)
        {
            var matched = _drafts.Values.Where(draft => MatchesDraftQuery(draft, query)).ToList();
            var total = matched.Count;
            IEnumerable<StudioPackageDraft> ordered = matched
                .OrderByDescending(static draft => draft.UpdatedAt)
                .ThenByDescending(static draft => draft.DraftId);

            if (StudioListCursor.TryDecode(query.Cursor, out var cursorUpdatedAt, out var cursorId))
            {
                ordered = ordered.Where(draft =>
                    draft.UpdatedAt < cursorUpdatedAt
                    || (draft.UpdatedAt == cursorUpdatedAt && draft.DraftId.CompareTo(cursorId) < 0));
            }

            var page = ordered.Take(limit + 1).ToList();
            string? nextCursor = null;
            if (page.Count > limit)
            {
                var lastKept = page[limit - 1];
                page.RemoveAt(page.Count - 1);
                nextCursor = StudioListCursor.Encode(lastKept.UpdatedAt, lastKept.DraftId);
            }

            IReadOnlyList<StudioPackageDraft> items = page;
            return Task.FromResult(new StudioPackageDraftListResult
            {
                Items = items,
                Total = total,
                NextCursor = nextCursor,
            });
        }
    }

    private static int ClampLimit(int requested)
        => requested <= 0 ? DefaultListLimit : Math.Min(requested, MaxListLimit);

    private static bool MatchesItemQuery(StudioContentItemRecord item, StudioContentItemQuery query)
    {
        if (query.Families is { Count: > 0 } families && !families.Contains(item.Family))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId) &&
            !string.Equals(item.WorkspaceId ?? string.Empty, query.WorkspaceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.OwnerId) &&
            !string.Equals(item.OwnerId ?? string.Empty, query.OwnerId, StringComparison.Ordinal))
        {
            return false;
        }

        if (query.States is { Count: > 0 } states && !states.Contains(ResolveState(item.CurrentVersionId, item.PublishedVersionId)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm) &&
            !item.PackageKey.Contains(query.SearchTerm.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesDraftQuery(StudioPackageDraft draft, StudioPackageDraftQuery query)
    {
        if (query.Families is { Count: > 0 } families && !families.Contains(draft.Family))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId) &&
            !string.Equals(draft.WorkspaceId ?? string.Empty, query.WorkspaceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.OwnerId) &&
            !string.Equals(draft.OwnerId ?? string.Empty, query.OwnerId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm) &&
            !draft.PackageKey.Contains(query.SearchTerm.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static StudioContentItemSummary ToSummary(StudioContentItemRecord item) => new()
    {
        ItemId = item.ItemId,
        PackageKey = item.PackageKey,
        WorkspaceId = item.WorkspaceId,
        Family = item.Family,
        State = ResolveState(item.CurrentVersionId, item.PublishedVersionId),
        CurrentVersionId = item.CurrentVersionId,
        PublishedVersionId = item.PublishedVersionId,
        OwnerId = item.OwnerId,
        CreatedBy = item.CreatedBy,
        UpdatedBy = item.UpdatedBy,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };

    private static StudioContentItemState ResolveState(Guid? currentVersionId, Guid? publishedVersionId)
        => publishedVersionId is not null
            ? StudioContentItemState.Published
            : currentVersionId is not null
                ? StudioContentItemState.Current
                : StudioContentItemState.Draft;

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
                version.OwnerId,
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
                    OwnerId = updatedItem.OwnerId,
                },
                RequestedBy = actorId,
                Reason = reason,
                CreatedAt = now,
            };
            _rollbackRequests.Add(request.RequestId, request);
            return Task.FromResult(request);
        }
    }

    private StudioContentItemRecord GetOrCreateItem(
        Guid itemId,
        string packageKey,
        string? workspaceId,
        StudioPackageFamily family,
        string? ownerId,
        string? actorId,
        DateTimeOffset timestamp)
    {
        if (_items.TryGetValue(itemId, out var item))
        {
            // Ownership is set once, at creation, and immutable thereafter (honua-server#3001):
            // an existing item's owner_id is never overwritten by a later draft/version upsert.
            return item;
        }

        return new StudioContentItemRecord(
            itemId,
            packageKey,
            workspaceId,
            family,
            CurrentVersionId: null,
            PublishedVersionId: null,
            OwnerId: ownerId,
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
        var conflicts = _items.Values.Any(item =>
            item.ItemId != draft.ItemId &&
            item.Family == draft.Family &&
            string.Equals(item.PackageKey, draft.PackageKey, StringComparison.Ordinal) &&
            string.Equals(item.WorkspaceId ?? string.Empty, draft.WorkspaceId ?? string.Empty, StringComparison.Ordinal));

        if (conflicts)
        {
            throw new InvalidOperationException("Studio package key conflicts with an existing content item.");
        }
    }

    private sealed record StudioContentItemRecord(
        Guid ItemId,
        string PackageKey,
        string? WorkspaceId,
        StudioPackageFamily Family,
        Guid? CurrentVersionId,
        Guid? PublishedVersionId,
        string? OwnerId,
        string? CreatedBy,
        string? UpdatedBy,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
