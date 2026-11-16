// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public static class StudioPackageHash
{
    public static string Compute(StudioPackageEnvelope envelope) => ComputeFromInput(ComputeInput(envelope));

    public static byte[] ComputeInput(StudioPackageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var stableEnvelope = envelope with
        {
            Validation = envelope.Validation with { GeneratedAt = null },
        };
        return JsonSerializer.SerializeToUtf8Bytes(stableEnvelope, StudioJsonContext.Default.StudioPackageEnvelope);
    }

    public static string ComputeFromInput(ReadOnlySpan<byte> input)
        => Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

    public static string? TryRebuildInput(StudioPackageEnvelope envelope, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var input = ComputeInput(envelope);
        return string.Equals(ComputeFromInput(input), contentHash, StringComparison.Ordinal)
            ? Convert.ToBase64String(input)
            : null;
    }
}
