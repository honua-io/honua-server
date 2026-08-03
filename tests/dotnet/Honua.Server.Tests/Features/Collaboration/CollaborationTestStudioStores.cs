// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Tests.Features.Collaboration;

/// <summary>
/// Studio package store that lets ONE other writer land between a draft update and whatever the
/// caller does next, reproducing the checkpoint race in honua-server#2999's review: the checkpoint
/// applies the replayed operations through <c>UpdateDraftAsync</c> and then versions the draft, but
/// the version save re-reads the draft, so a Studio draft update arriving in that window would be
/// versioned in place of the replay — while the checkpoint still advanced the op-log cursor, making
/// those operations invisible to every later checkpoint.
/// </summary>
/// <remarks>
/// The interleaved write replaces the composition body exactly as a competing Studio draft update
/// would, and it is armed once so only the checkpoint's apply is raced; the version save's own
/// internal update runs unmolested.
/// </remarks>
internal sealed class ConcurrentUpdateAfterDraftWriteStore : IStudioPackageStore
{
    private readonly IStudioPackageStore _inner;
    private readonly JsonElement _competingBody;
    private int _armed;

    public ConcurrentUpdateAfterDraftWriteStore(IStudioPackageStore inner, JsonElement competingBody)
    {
        _inner = inner;
        _competingBody = competingBody;
    }

    /// <summary>Arms exactly one interleaved competing draft update.</summary>
    public void ArmOnce() => Interlocked.Exchange(ref _armed, 1);

    public StudioPackagePersistenceMode PersistenceMode => _inner.PersistenceMode;

    public async Task<StudioPackageDraft?> UpdateDraftAsync(
        StudioPackageDraft draft,
        CancellationToken cancellationToken = default)
    {
        var updated = await _inner.UpdateDraftAsync(draft, cancellationToken).ConfigureAwait(false);
        if (updated is null || Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return updated;
        }

        // Another writer commits a full-body draft update against the generation this call just
        // produced. Everything goes through the inner store so the interleaved write cannot
        // re-trigger the arming logic.
        var current = await _inner.GetDraftAsync(updated.DraftId, cancellationToken).ConfigureAwait(false) ?? updated;
        _ = await _inner.UpdateDraftAsync(
            current with { Envelope = current.Envelope with { Body = _competingBody } },
            cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public Task<StudioPackageDraft> CreateDraftAsync(
        StudioPackageDraft draft,
        CancellationToken cancellationToken = default) => _inner.CreateDraftAsync(draft, cancellationToken);

    public Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        => _inner.GetDraftAsync(draftId, cancellationToken);

    public Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        => _inner.DeleteDraftAsync(draftId, cancellationToken);

    public Task<StudioContentVersion> CreateVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
        => _inner.CreateVersionAsync(draft, changeNote, actorId, cancellationToken);

    public Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) => _inner.ListVersionsAsync(itemId, cancellationToken);

    public Task<StudioContentVersion?> GetVersionAsync(
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken = default) => _inner.GetVersionAsync(itemId, versionId, cancellationToken);

    public Task<StudioContentItemPointers?> GetPointersAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) => _inner.GetPointersAsync(itemId, cancellationToken);

    public Task<StudioContentItemListResult> ListContentItemsAsync(
        StudioContentItemQuery query,
        CancellationToken cancellationToken = default) => _inner.ListContentItemsAsync(query, cancellationToken);

    public Task<StudioPackageDraftListResult> ListDraftsAsync(
        StudioPackageDraftQuery query,
        CancellationToken cancellationToken = default) => _inner.ListDraftsAsync(query, cancellationToken);

    public Task<StudioPublicationRequest> CreatePublicationRequestAsync(
        StudioPublicationRequest request,
        CancellationToken cancellationToken = default)
        => _inner.CreatePublicationRequestAsync(request, cancellationToken);

    public Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default)
        => _inner.RollbackAsync(itemId, targetVersionId, target, actorId, reason, cancellationToken);
}

/// <summary>
/// Studio package store that deletes a draft after the checkpoint apply succeeds and the
/// lifecycle version-save reads that generation, reproducing the deletion window between
/// <c>SaveDraftAsVersionAsync</c>'s read and validation update.
/// </summary>
internal sealed class DeleteDuringVersionSaveStore : IStudioPackageStore
{
    private readonly IStudioPackageStore _inner;
    private int _armAfterNextUpdate;
    private int _deleteAfterNextRead;
    private int _deleteWasTriggered;

    public DeleteDuringVersionSaveStore(IStudioPackageStore inner) => _inner = inner;

    /// <summary>Arms exactly one deletion after the next successful draft update.</summary>
    public void ArmOnce() => Interlocked.Exchange(ref _armAfterNextUpdate, 1);

    /// <summary>Whether the deterministic deletion seam ran.</summary>
    public bool DeleteWasTriggered => Volatile.Read(ref _deleteWasTriggered) != 0;

    public StudioPackagePersistenceMode PersistenceMode => _inner.PersistenceMode;

    public async Task<StudioPackageDraft?> UpdateDraftAsync(
        StudioPackageDraft draft,
        CancellationToken cancellationToken = default)
    {
        var updated = await _inner.UpdateDraftAsync(draft, cancellationToken).ConfigureAwait(false);
        if (updated is not null && Interlocked.Exchange(ref _armAfterNextUpdate, 0) != 0)
        {
            Interlocked.Exchange(ref _deleteAfterNextRead, 1);
        }

        return updated;
    }

    public Task<StudioPackageDraft> CreateDraftAsync(
        StudioPackageDraft draft,
        CancellationToken cancellationToken = default) => _inner.CreateDraftAsync(draft, cancellationToken);

    public async Task<StudioPackageDraft?> GetDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = await _inner.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is not null && Interlocked.Exchange(ref _deleteAfterNextRead, 0) != 0)
        {
            _ = await _inner.DeleteDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _deleteWasTriggered, 1);
        }

        return draft;
    }

    public Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        => _inner.DeleteDraftAsync(draftId, cancellationToken);

    public Task<StudioContentVersion> CreateVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
        => _inner.CreateVersionAsync(draft, changeNote, actorId, cancellationToken);

    public Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) => _inner.ListVersionsAsync(itemId, cancellationToken);

    public Task<StudioContentVersion?> GetVersionAsync(
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken = default) => _inner.GetVersionAsync(itemId, versionId, cancellationToken);

    public Task<StudioContentItemPointers?> GetPointersAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) => _inner.GetPointersAsync(itemId, cancellationToken);

    public Task<StudioContentItemListResult> ListContentItemsAsync(
        StudioContentItemQuery query,
        CancellationToken cancellationToken = default) => _inner.ListContentItemsAsync(query, cancellationToken);

    public Task<StudioPackageDraftListResult> ListDraftsAsync(
        StudioPackageDraftQuery query,
        CancellationToken cancellationToken = default) => _inner.ListDraftsAsync(query, cancellationToken);

    public Task<StudioPublicationRequest> CreatePublicationRequestAsync(
        StudioPublicationRequest request,
        CancellationToken cancellationToken = default)
        => _inner.CreatePublicationRequestAsync(request, cancellationToken);

    public Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default)
        => _inner.RollbackAsync(itemId, targetVersionId, target, actorId, reason, cancellationToken);
}
