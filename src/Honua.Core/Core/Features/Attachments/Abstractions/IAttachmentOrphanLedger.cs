// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Abstractions;

public enum AttachmentOrphanKind
{
    ObjectWithoutMetadata,

    UndeletedObject
}

// A ledger failure must not hide the original error or turn a successful delete into a failure.
public interface IAttachmentOrphanLedger
{
    ValueTask RecordAsync(AttachmentOrphan orphan, CancellationToken cancellationToken = default);
}

public readonly record struct AttachmentOrphan(
    string StoragePath,
    int LayerId,
    long FeatureId,
    AttachmentOrphanKind Kind,
    DateTimeOffset DetectedAt,
    string? Reason);
