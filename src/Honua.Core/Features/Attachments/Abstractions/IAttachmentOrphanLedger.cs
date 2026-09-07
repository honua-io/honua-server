// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Abstractions;

/// <summary>
/// How an attachment object and its metadata row came to disagree.
/// </summary>
public enum AttachmentOrphanKind
{
    /// <summary>
    /// The object was uploaded, the metadata insert failed, and the compensating
    /// delete of the uploaded object also failed. The object exists with no row.
    /// </summary>
    ObjectWithoutMetadata,

    /// <summary>
    /// The metadata row was deleted (or repointed) and committed, but the stored
    /// object could not be removed. The object exists with no row referencing it.
    /// </summary>
    UndeletedObject
}

/// <summary>
/// Records attachment objects that outlived their metadata row.
/// </summary>
/// <remarks>
/// <para>
/// Attachment writes span two systems — object storage and the metadata table — with no
/// shared transaction and no two-phase protocol. Each write path therefore has a
/// compensating action that can itself fail (a storage outage during cleanup, a process
/// exit between the two steps). Before this abstraction those failures were swallowed
/// into a warning log line, which means an operator could neither alert on them nor
/// enumerate what needed reconciling.
/// </para>
/// <para>
/// Implementations must be non-throwing: a ledger failure must never mask the original
/// error being propagated to the caller, nor turn a successful delete into a failure.
/// </para>
/// </remarks>
public interface IAttachmentOrphanLedger
{
    /// <summary>
    /// Records one orphaned storage object for later reconciliation.
    /// </summary>
    /// <param name="orphan">The orphaned object and the circumstances that produced it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RecordAsync(AttachmentOrphan orphan, CancellationToken cancellationToken = default);
}

/// <summary>
/// One storage object that is no longer reachable through attachment metadata.
/// </summary>
/// <param name="StoragePath">Storage identifier of the object that outlived its row.</param>
/// <param name="LayerId">Layer the attachment belonged to.</param>
/// <param name="FeatureId">Feature the attachment belonged to.</param>
/// <param name="Kind">How the divergence arose.</param>
/// <param name="DetectedAt">When the divergence was observed.</param>
/// <param name="Reason">Human-readable cause, typically the failing exception's message.</param>
public readonly record struct AttachmentOrphan(
    string StoragePath,
    int LayerId,
    long FeatureId,
    AttachmentOrphanKind Kind,
    DateTimeOffset DetectedAt,
    string? Reason);
