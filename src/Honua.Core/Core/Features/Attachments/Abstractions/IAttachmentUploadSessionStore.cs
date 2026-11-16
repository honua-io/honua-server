// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;

namespace Honua.Core.Features.Attachments.Abstractions;

public interface IAttachmentUploadSessionStore
{
    Task<AttachmentUploadSession> BeginAsync(AttachmentUploadRequest request, CancellationToken cancellationToken = default);
    Task<AttachmentUploadSession> GetAsync(AttachmentUploadSessionKey key, CancellationToken cancellationToken = default);
    Task<AttachmentUploadSession> AppendAsync(AttachmentUploadChunk chunk, CancellationToken cancellationToken = default);
    Task CopyContentAsync(AttachmentUploadSessionKey key, Stream destination, CancellationToken cancellationToken = default);
    Task<Attachment> CommitAsync(AttachmentUploadSessionKey key, string preparedStoragePath, CancellationToken cancellationToken = default);
    Task<Attachment> CommitAsync(AttachmentUploadSessionKey key, Func<AttachmentUploadSession, CancellationToken, Task<string>> publishContent, CancellationToken cancellationToken = default);
    Task AbortAsync(AttachmentUploadSessionKey key, CancellationToken cancellationToken = default);
}
