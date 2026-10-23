// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Computes deterministic hashes for immutable Studio package envelopes.
/// </summary>
/// <remarks>
/// A version's <c>contentHash</c> is the lower-case hex SHA-256 of its canonical hash input:
/// the UTF-8 bytes of the server's serialization of the whole envelope with the volatile
/// <c>validation.generatedAt</c> timestamp removed. Those bytes depend on serializer details, so
/// the version read returns them verbatim as <c>contentHashInput</c> (base64) and a client
/// recomputes the digest from them instead of re-serializing the envelope (honua-server#5449).
/// </remarks>
public static class StudioPackageHash
{
    /// <summary>
    /// Computes a lower-case hex SHA-256 hash for a package envelope, excluding volatile validation timestamps.
    /// </summary>
    public static string Compute(StudioPackageEnvelope envelope) => ComputeFromInput(ComputeInput(envelope));

    /// <summary>
    /// Builds the canonical hash input for a package envelope: the exact bytes <see cref="Compute"/> hashes.
    /// </summary>
    public static byte[] ComputeInput(StudioPackageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var stableEnvelope = envelope with
        {
            Validation = envelope.Validation with { GeneratedAt = null },
        };
        return JsonSerializer.SerializeToUtf8Bytes(stableEnvelope, StudioJsonContext.Default.StudioPackageEnvelope);
    }

    /// <summary>Computes the lower-case hex SHA-256 hash of a canonical hash input.</summary>
    public static string ComputeFromInput(ReadOnlySpan<byte> input)
        => Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

    /// <summary>
    /// Rebuilds the base64 canonical hash input for a version whose input was not persisted
    /// (one saved before the input was stored). The envelope is re-serialized and the result is
    /// returned only when it hashes to <paramref name="contentHash"/>: a store that does not
    /// round-trip the envelope byte-for-byte (jsonb reorders object keys) cannot reproduce the
    /// input, and a client must never be handed bytes that do not recompute to the digest.
    /// </summary>
    public static string? TryRebuildInput(StudioPackageEnvelope envelope, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var input = ComputeInput(envelope);
        return string.Equals(ComputeFromInput(input), contentHash, StringComparison.Ordinal)
            ? Convert.ToBase64String(input)
            : null;
    }
}
