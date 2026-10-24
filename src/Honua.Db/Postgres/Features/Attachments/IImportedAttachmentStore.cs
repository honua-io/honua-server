// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    /// <summary>
    /// Points imported attachments at the object ids assigned by a table replacement.
    /// <paramref name="sourceObjectIds"/> maps each source object id to the new target object id.
    /// An imported attachment whose parent was not loaded, and whose current feature id was
    /// reused by a loaded feature, is detached so it does not appear on that feature.
    /// Attachments created through Honua have no source parent and are left unchanged.
    /// </summary>
    Task RemapImportedAttachmentsAsync(
        int layerId,
        IReadOnlyDictionary<long, long> sourceObjectIds,
        CancellationToken cancellationToken);
}
