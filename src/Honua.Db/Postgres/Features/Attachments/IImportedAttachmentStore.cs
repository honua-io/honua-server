// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;

namespace Honua.Db.Postgres.Features.Attachments;

/// <summary>Persists source identity separately from attachments created through Honua.</summary>
internal interface IImportedAttachmentStore
{
    /// <summary>Detects legacy rows whose ownership must be reviewed before copying another set.</summary>
    Task<bool> HasLegacyAttachmentsAsync(int layerId, CancellationToken cancellationToken);

    Task<Attachment> UploadImportedAsync(
        int layerId, long featureId, string source, long sourceParentId, long sourceAttachmentId,
        Guid generation, string filename, string contentType, Stream content, string? keywords,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes prior imported rows only after every source parent and attachment was verified.
    /// Returns whether untracked attachments require operator review for legacy provenance.
    /// </summary>
    Task<bool> CompleteImportAsync(int layerId, Guid generation, CancellationToken cancellationToken);
}
