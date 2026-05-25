// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Computes deterministic hashes for immutable Studio package envelopes.
/// </summary>
public static class StudioPackageHash
{
    /// <summary>
    /// Computes a lower-case hex SHA-256 hash for a package envelope, excluding volatile validation timestamps.
    /// </summary>
    public static string Compute(StudioPackageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var stableEnvelope = envelope with
        {
            Validation = envelope.Validation with { GeneratedAt = null },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stableEnvelope, StudioJsonContext.Default.StudioPackageEnvelope);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
