// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Domain;

/// <summary>Immutable conditions sealed into a saved-version restoration proposal.</summary>
public sealed record StudioRollbackPreconditions
{
    /// <summary>Canonical request identity assigned by the mutation runtime.</summary>
    public Guid RequestId { get; init; }

    /// <summary>Content hash of the original saved version to restore.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Current pointer observed before the proposal; null explicitly means no current version.</summary>
    public required Guid? ExpectedCurrentVersionId { get; init; }

    /// <summary>Observed pointer revision; changes back to an earlier pointer still invalidate approval.</summary>
    public required long ExpectedPointerRevision { get; init; }

    /// <summary>Rejects incomplete conditions before any effect.</summary>
    public void Validate()
    {
        if (RequestId == Guid.Empty || string.IsNullOrWhiteSpace(ContentHash) ||
            ExpectedPointerRevision < 0 || ExpectedCurrentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Restoration requires a canonical request, saved content hash and observed pointer revision.");
        }
    }

    /// <summary>Checks the approved snapshot while the native store holds its item write lock.</summary>
    public void RequireCurrent(StudioContentItemPointers pointers, StudioContentVersion version)
    {
        if (!string.Equals(version.ContentHash, ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved version content hash does not match the approved restoration.");
        }
        if (pointers.CurrentVersionId != ExpectedCurrentVersionId || pointers.PointerRevision != ExpectedPointerRevision)
        {
            throw new InvalidOperationException("The Studio item pointers changed after restoration was proposed; refresh and request a new approval.");
        }
    }

    /// <summary>Rejects reuse of a committed request identity for a different effect.</summary>
    public void RequireSameRequest(StudioRollbackRequest receipt, Guid itemId, Guid versionId,
        StudioRollbackPointer target, string? actorId, string? reason)
    {
        if (receipt.Preconditions != this || receipt.RequestId != RequestId || receipt.ItemId != itemId ||
            receipt.TargetVersionId != versionId || receipt.Target != target ||
            !string.Equals(receipt.RequestedBy, actorId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Reason, reason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Studio rollback request ID is already bound to different content.");
        }
    }
}
