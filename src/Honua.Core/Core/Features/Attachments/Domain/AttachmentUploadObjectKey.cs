// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

public static class AttachmentUploadObjectKey
{
    public const string Prefix = "attachments/native/";

    public static string ForSession(Guid sessionId) => $"{Prefix}{sessionId:N}";
}
