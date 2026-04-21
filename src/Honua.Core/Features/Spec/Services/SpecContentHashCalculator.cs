// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Computes the content hash for a spec node. The hash is the single source
/// of truth for cache identity; it is derived from grammar + process-family
/// versions, the canonical node fragment, and the sorted hashes of the
/// node's inputs.
/// </summary>
internal static class SpecContentHashCalculator
{
    private const char Separator = ''; // ASCII unit separator
    private const char ItemSeparator = ''; // ASCII record separator

    /// <summary>
    /// Computes the content hash for <paramref name="node"/> given its input
    /// hashes. Inputs are sorted by key to guarantee stability across input
    /// reorderings.
    /// </summary>
    public static string Compute(
        string grammarVersion,
        string processFamilyVersion,
        CanonicalSpecNode node,
        IReadOnlyDictionary<string, string> inputHashes)
    {
        ArgumentNullException.ThrowIfNull(grammarVersion);
        ArgumentNullException.ThrowIfNull(processFamilyVersion);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(inputHashes);

        var builder = new StringBuilder(256);
        builder.Append(grammarVersion).Append(Separator);
        builder.Append(processFamilyVersion).Append(Separator);
        builder.Append(node.Id).Append(Separator);
        builder.Append(node.Kind).Append(Separator);
        builder.Append(node.Op ?? string.Empty).Append(Separator);

        // Canonical fragment participates when provided. Fall back to
        // deterministic serialisation of Parameters + SourcePins so the hash
        // remains stable even without an upstream-canonicalised fragment.
        if (!string.IsNullOrEmpty(node.CanonicalFragment))
        {
            builder.Append(node.CanonicalFragment).Append(Separator);
        }
        else
        {
            AppendSortedPairs(builder, node.Parameters);
            builder.Append(Separator);
            AppendSortedPairs(builder, node.SourcePins);
            builder.Append(Separator);
        }

        // Sort input hashes by key for deterministic ordering.
        foreach (var key in inputHashes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(key).Append('=').Append(inputHashes[key]).Append(ItemSeparator);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(builder.Length));
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(builder.ToString(), buffer);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buffer.AsSpan(0, byteCount), hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendSortedPairs(StringBuilder builder, IReadOnlyDictionary<string, string> pairs)
    {
        foreach (var key in pairs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(key).Append('=').Append(pairs[key]).Append(ItemSeparator);
        }
    }

    /// <summary>
    /// Computes a raw sha256 hex digest over the given UTF-8 bytes. Used by
    /// the in-memory artifact cache when it needs to verify content.
    /// </summary>
    public static string HashBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }
}
