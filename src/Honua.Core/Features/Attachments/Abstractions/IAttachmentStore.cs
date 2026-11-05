// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Domain;

namespace Honua.Core.Features.Attachments.Abstractions;

public interface IAttachmentStore
{
    // Query operations

    Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default);

    Task<Attachment[]> ListAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    // Edit operations

    Task<Attachment> CreateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default);

    Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default);

    Task<Attachment> UpdateKeywordsAsync(int layerId, long featureId, long attachmentId, string? keywords, CancellationToken cancellationToken = default);

    Task<Attachment> ReplaceAsync(
        int layerId,
        long featureId,
        long attachmentId,
        string filename,
        string contentType,
        Stream content,
        string? keywords = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default);

    // File operations

    Task<Attachment> UploadAsync(int layerId, long featureId, string filename, string contentType, Stream content, string? keywords = null, CancellationToken cancellationToken = default);

    Task<AttachmentContent?> DownloadAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default);
}
