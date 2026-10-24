// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog;

/// <summary>Process-lifetime audit-chain key shared by writers and verifiers.</summary>
public sealed class AuditChainKeySnapshot
{
    /// <summary>Decodes and retains the startup key; changes require a process restart.</summary>
    /// <param name="configuredKey">The configured base64 key, or null.</param>
    public AuditChainKeySnapshot(string? configuredKey)
    {
        Key = AuditChainKeyMaterial.Decode(configuredKey);
    }

    /// <summary>The startup key bytes, or empty when no key was configured.</summary>
    public ReadOnlyMemory<byte> Key { get; }
}
