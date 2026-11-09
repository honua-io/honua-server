// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public static class AuditChainKeyMaterial
{
    public const int MinimumLength = 32;

    public static ReadOnlyMemory<byte> Decode(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "AuditLog:ChainVerification:Key must be a base64 value.",
                exception);
        }

        if (bytes.Length < MinimumLength)
        {
            throw new InvalidOperationException(
                "AuditLog:ChainVerification:Key must decode to at least 32 bytes.");
        }

        return bytes;
    }
}
