// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog;

public sealed class AuditChainKeySnapshot
{
    public AuditChainKeySnapshot(string? configuredKey)
    {
        Key = AuditChainKeyMaterial.Decode(configuredKey);
    }

    public ReadOnlyMemory<byte> Key { get; }
}
