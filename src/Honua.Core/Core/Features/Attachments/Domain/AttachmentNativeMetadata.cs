// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

public sealed record AttachmentNativeMetadata(Guid GlobalId, string[] Keywords, byte[] Sha256);

public sealed record AttachmentNativeMetadataQuery(int LayerId, long FeatureId, long[] AttachmentIds);
