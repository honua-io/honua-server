// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Infrastructure.Caching;

internal sealed class ETagService : IETagService
{
    private const int HashSize = 32; // SHA256 hash size in bytes
    private const int Base64HashSize = 44; // Base64-encoded SHA256 size (rounded up to nearest 4-byte boundary)
    // Base64-encoded SHA256 hash of empty content with padding trimmed to match ComputeETag formatting.
    private const string EmptyContentHash = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";
    private const string EmptyETag = "\"" + EmptyContentHash + "\"";

    public string ComputeETag<T>(T obj, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        using var payload = ETagJsonPayload.Serialize(obj, jsonTypeInfo);
        return ComputeETag(payload.WrittenSpan);
    }

    public string ComputeETag(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            // Return a consistent ETag for empty content (SHA256 hash of empty payload).
            return EmptyETag;
        }

        // Compute SHA256 hash
        Span<byte> hashBytes = stackalloc byte[HashSize];
        if (!SHA256.TryHashData(data, hashBytes, out _))
        {
            throw new InvalidOperationException("Failed to compute SHA256 hash");
        }

        // Convert to Base64 directly into a quoted buffer and trim any trailing '=' padding.
        Span<char> etagChars = stackalloc char[Base64HashSize + 2];
        etagChars[0] = '"';
        if (!Convert.TryToBase64Chars(hashBytes, etagChars[1..], out var charsWritten))
        {
            throw new InvalidOperationException("Failed to encode hash as Base64");
        }

        while (charsWritten > 0 && etagChars[charsWritten] == '=')
        {
            charsWritten--;
        }

        etagChars[charsWritten + 1] = '"';
        return new string(etagChars[..(charsWritten + 2)]);
    }

    public string ComputeETag(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrEmpty(content))
        {
            return ComputeETag(ReadOnlySpan<byte>.Empty);
        }

        // Convert string to UTF-8 bytes
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(content.Length);

        if (maxByteCount <= 256) // Use stack allocation for small strings
        {
            Span<byte> utf8Bytes = stackalloc byte[maxByteCount];
            var actualByteCount = Encoding.UTF8.GetBytes(content, utf8Bytes);
            return ComputeETag(utf8Bytes[..actualByteCount]);
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var actualByteCount = Encoding.UTF8.GetBytes(content, rented);
            return ComputeETag(rented.AsSpan(0, actualByteCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public bool IsModified(string? ifNoneMatch, string currentETag)
    {
        if (string.IsNullOrEmpty(ifNoneMatch))
        {
            return true; // No conditional header, resource is considered modified
        }

        var headerSpan = ifNoneMatch.AsSpan();
        // Handle "*" which means any ETag
        if (headerSpan.Trim().SequenceEqual("*"))
        {
            return false; // Resource exists (any ETag), so not modified
        }

        // If-None-Match uses weak comparison for GET/HEAD.
        var currentValue = ParseETag(currentETag.AsSpan()).Value;
        foreach (var range in headerSpan.Split(','))
        {
            var candidate = headerSpan[range].Trim();
            if (candidate.IsEmpty)
            {
                continue;
            }

            if (ParseETag(candidate).Value.SequenceEqual(currentValue))
            {
                return false; // ETag matches, resource not modified
            }
        }

        return true; // No matching ETag found, resource is modified
    }

    public bool MatchesPrecondition(string? ifMatch, string currentETag)
    {
        if (string.IsNullOrEmpty(ifMatch))
        {
            return true; // No conditional header, precondition passes
        }

        var headerSpan = ifMatch.AsSpan();
        // Handle "*" which means any ETag
        if (headerSpan.Trim().SequenceEqual("*"))
        {
            return true; // Resource exists (any ETag), precondition passes
        }

        // If-Match requires strong comparison; weak tags never match.
        var current = ParseETag(currentETag.AsSpan());
        if (current.IsWeak)
        {
            return false;
        }

        foreach (var range in headerSpan.Split(','))
        {
            var candidate = headerSpan[range].Trim();
            if (candidate.IsEmpty)
            {
                continue;
            }

            var candidateTag = ParseETag(candidate);
            if (!candidateTag.IsWeak && candidateTag.Value.SequenceEqual(current.Value))
            {
                return true; // ETag matches, precondition passes
            }
        }

        return false; // No matching ETag found, precondition fails
    }

    public void SetCacheHeaders(HttpResponse response, string etag, DateTimeOffset? lastModified = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(etag);

        // Set ETag header
        response.Headers.ETag = etag;

        // Set Last-Modified header if provided
        if (lastModified.HasValue)
        {
            response.Headers.LastModified = lastModified.Value.ToString("R"); // RFC 1123 format
        }

        // Set Cache-Control to enable validation caching
        if (!response.Headers.ContainsKey("Cache-Control"))
        {
            response.Headers.CacheControl = "private, must-revalidate";
        }
    }

    private static ParsedETag ParseETag(ReadOnlySpan<char> etag)
    {
        var trimmed = etag.Trim();
        if (trimmed.IsEmpty)
        {
            return ParsedETag.Strong(ReadOnlySpan<char>.Empty);
        }

        var isWeak = false;
        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            isWeak = true;
            trimmed = trimmed[2..].TrimStart();
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return isWeak ? ParsedETag.Weak(trimmed) : ParsedETag.Strong(trimmed);
    }

    private readonly ref struct ParsedETag
    {
        public ReadOnlySpan<char> Value { get; init; }

        public bool IsWeak { get; init; }

        public static ParsedETag Strong(ReadOnlySpan<char> value) => new() { Value = value };

        public static ParsedETag Weak(ReadOnlySpan<char> value) => new() { Value = value, IsWeak = true };
    }
}
