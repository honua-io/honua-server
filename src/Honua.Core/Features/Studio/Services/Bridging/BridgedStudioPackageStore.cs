// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services.Bridging;

/// <summary>
/// <see cref="IStudioPackageStore"/> decorator that routes bridged families (ADR-0069,
/// honua-server#3004) to their <see cref="IStudioFamilyPersistenceBridge"/> adapters while
/// every other operation delegates to the inner Studio store. Drafts always live in the inner
/// store (they are Studio working state with <c>generation</c> concurrency); immutable
/// versions, item pointers, publication, and enumeration for bridged families read and write
/// through the family's native store.
/// </summary>
public sealed class BridgedStudioPackageStore : IStudioPackageStore
{
    private static readonly StudioPackageFamily[] AllFamilies = Enum.GetValues<StudioPackageFamily>();

    private readonly IStudioPackageStore _inner;
    private readonly Dictionary<StudioPackageFamily, IStudioFamilyPersistenceBridge> _bridges;

    /// <summary>Initializes the bridged store decorator.</summary>
    public BridgedStudioPackageStore(IStudioPackageStore inner, IEnumerable<IStudioFamilyPersistenceBridge> bridges)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(bridges);
        _inner = inner;
        _bridges = bridges.ToDictionary(static bridge => bridge.Family);
    }

    /// <inheritdoc />
    public StudioPackagePersistenceMode PersistenceMode => _inner.PersistenceMode;

    /// <inheritdoc />
    public Task<StudioPackageDraft> CreateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default)
        => _inner.CreateDraftAsync(draft, cancellationToken);

    /// <inheritdoc />
    public Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        => _inner.GetDraftAsync(draftId, cancellationToken);

    /// <inheritdoc />
    public Task<StudioPackageDraft?> UpdateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default)
        => _inner.UpdateDraftAsync(draft, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        => _inner.DeleteDraftAsync(draftId, cancellationToken);

    /// <inheritdoc />
    public Task<StudioContentVersion> CreateVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return _bridges.TryGetValue(draft.Family, out var bridge)
            ? bridge.SaveVersionAsync(draft, changeNote, actorId, cancellationToken)
            : _inner.CreateVersionAsync(draft, changeNote, actorId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        foreach (var bridge in _bridges.Values)
        {
            var versions = await bridge.ListVersionsAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (versions.Count > 0)
            {
                return versions;
            }
        }

        return await _inner.ListVersionsAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default)
    {
        foreach (var bridge in _bridges.Values)
        {
            var version = await bridge.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                return version;
            }
        }

        return await _inner.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var bridged = await ResolveBridgedPointersAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (bridged is not null)
        {
            return bridged.Pointers;
        }

        return await _inner.GetPointersAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioContentItemListResult> ListContentItemsAsync(
        StudioContentItemQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requestedFamilies = query.Families is { Count: > 0 } families ? families : AllFamilies;
        var innerFamilies = requestedFamilies.Where(family => !_bridges.ContainsKey(family)).ToArray();
        var bridgedFamilies = requestedFamilies.Where(_bridges.ContainsKey).Distinct().ToArray();

        // The inner store never lists bridged families: bridged items surface from their native
        // stores below, and any inner shadow rows (created for lifecycle drafts before their
        // first save) stay out of content-item enumeration (ADR-0069 documented divergence).
        var innerResult = innerFamilies.Length > 0
            ? await _inner.ListContentItemsAsync(query with { Families = innerFamilies }, cancellationToken).ConfigureAwait(false)
            : new StudioContentItemListResult { Items = Array.Empty<StudioContentItemSummary>(), Total = 0, NextCursor = null };

        if (bridgedFamilies.Length == 0)
        {
            return innerResult;
        }

        var bridgedMatches = new List<StudioContentItemSummary>();
        foreach (var family in bridgedFamilies)
        {
            var summaries = await _bridges[family].ListItemSummariesAsync(cancellationToken).ConfigureAwait(false);
            bridgedMatches.AddRange(summaries.Where(summary => MatchesQuery(summary, query)));
        }

        var total = innerResult.Total + bridgedMatches.Count;

        IEnumerable<StudioContentItemSummary> bridgedPage = bridgedMatches;
        if (StudioListCursor.TryDecode(query.Cursor, out var cursorUpdatedAt, out var cursorId))
        {
            bridgedPage = bridgedPage.Where(item =>
                item.UpdatedAt < cursorUpdatedAt
                || (item.UpdatedAt == cursorUpdatedAt && item.ItemId.CompareTo(cursorId) < 0));
        }

        // The inner page holds the top-N inner rows after the cursor and bridgedPage holds every
        // bridged row after the cursor, so the merged top-N is the true top-N of the union.
        var limit = query.Limit <= 0 ? 25 : query.Limit;
        var merged = innerResult.Items
            .Concat(bridgedPage)
            .OrderByDescending(static item => item.UpdatedAt)
            .ThenByDescending(static item => item.ItemId)
            .ToList();

        var hasMore = merged.Count > limit || innerResult.NextCursor is not null;
        var page = merged.Take(limit).ToArray();
        var nextCursor = hasMore && page.Length > 0
            ? StudioListCursor.Encode(page[^1].UpdatedAt, page[^1].ItemId)
            : null;

        return new StudioContentItemListResult
        {
            Items = page,
            Total = total,
            NextCursor = nextCursor,
        };
    }

    /// <inheritdoc />
    public Task<StudioPackageDraftListResult> ListDraftsAsync(StudioPackageDraftQuery query, CancellationToken cancellationToken = default)
        => _inner.ListDraftsAsync(query, cancellationToken);

    /// <inheritdoc />
    public async Task<StudioPublicationRequest> CreatePublicationRequestAsync(
        StudioPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bridged = await ResolveBridgedPointersAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (bridged is not null)
        {
            return await bridged.Bridge.PublishAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await _inner.CreatePublicationRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var bridged = await ResolveBridgedPointersAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (bridged is not null)
        {
            throw new InvalidOperationException(
                $"Rollback is not supported for the bridged {bridged.Bridge.Family} family; reopen the target version and save or republish instead.");
        }

        return await _inner.RollbackAsync(itemId, targetVersionId, target, actorId, reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BridgedItem?> ResolveBridgedPointersAsync(Guid itemId, CancellationToken cancellationToken)
    {
        foreach (var bridge in _bridges.Values)
        {
            var pointers = await bridge.GetPointersAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (pointers is not null)
            {
                return new BridgedItem(bridge, pointers);
            }
        }

        return null;
    }

    private static bool MatchesQuery(StudioContentItemSummary item, StudioContentItemQuery query)
    {
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

        if (query.States is { Count: > 0 } states && !states.Contains(item.State))
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

    private sealed record BridgedItem(IStudioFamilyPersistenceBridge Bridge, StudioContentItemPointers Pointers);
}
