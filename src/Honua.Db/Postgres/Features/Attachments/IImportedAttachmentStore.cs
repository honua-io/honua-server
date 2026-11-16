// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;

namespace Honua.Db.Postgres.Features.Attachments;

internal interface IImportedAttachmentStore
{
    Task<bool> HasLegacyAttachmentsAsync(int layerId, CancellationToken cancellationToken);

    Task<Attachment> UploadImportedAsync(
        int layerId, long featureId, string source, long sourceParentId, long sourceAttachmentId,
        Guid generation, string filename, string contentType, Stream content, string? keywords,
        CancellationToken cancellationToken);

    Task<bool> CompleteImportAsync(int layerId, Guid generation, CancellationToken cancellationToken);

    Task RemapImportedAttachmentsAsync(
        int layerId,
        IReadOnlyDictionary<long, long> sourceObjectIds,
        CancellationToken cancellationToken);
}
