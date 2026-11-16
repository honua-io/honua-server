// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;

namespace Honua.Core.Features.Attachments.Abstractions;

public interface IAttachmentUploadPublisher
{
    Task<Attachment> PublishAsync(AttachmentUploadSession session, CancellationToken cancellationToken = default);
}
