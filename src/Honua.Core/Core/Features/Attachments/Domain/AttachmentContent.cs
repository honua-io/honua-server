// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

public readonly record struct AttachmentContent
{
    public required Attachment Attachment { get; init; }

    public required Stream Content { get; init; }

    public static AttachmentContent Create(Attachment attachment, Stream content)
        => new()
        {
            Attachment = attachment,
            Content = content
        };
}
