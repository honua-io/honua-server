// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Infrastructure.Caching;

internal sealed class ETagService : IETagService
{
    private const int HashSize = 32; // SHA256 hash size in bytes
    private const int Base64HashSize = 44; // Base64-encoded SHA256 size (rounded up to nearest 4-byte boundary)
    // Base64-encoded SHA256 hash of empty content with padding trimmed to match ComputeETag formatting.
    private const string EmptyContentHash = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

    public string ComputeETag<T>(T obj, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        // Serialize the object to JSON bytes for hashing
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(obj, jsonTypeInfo);

        return ComputeETag(jsonBytes);
    }

    public string ComputeETag(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            // Return a consistent ETag for empty content (SHA256 hash of empty payload).
            return $"\"{EmptyContentHash}\"";
        }

        // Compute SHA256 hash
        Span<byte> hashBytes = stackalloc byte[HashSize];
        if (!SHA256.TryHashData(data, hashBytes, out _))
        {
            throw new InvalidOperationException("Failed to compute SHA256 hash");
        }

        // Convert to Base64 for compact representation
        Span<char> base64Chars = stackalloc char[Base64HashSize];
        if (!Convert.TryToBase64Chars(hashBytes, base64Chars, out var charsWritten))
        {
            throw new InvalidOperationException("Failed to encode hash as Base64");
        }

        // Return as quoted strong ETag (trim any padding and quote)
        var base64Hash = new string(base64Chars[..charsWritten]).TrimEnd('=');
        return $"\"{base64Hash}\"";
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
        else
        {
            // Use heap allocation for larger strings
            var utf8Bytes = Encoding.UTF8.GetBytes(content);
            return ComputeETag(utf8Bytes);
        }
    }

    public bool IsModified(string? ifNoneMatch, string currentETag)
    {
        if (string.IsNullOrEmpty(ifNoneMatch))
        {
            return true; // No conditional header, resource is considered modified
        }

        // Handle "*" which means any ETag
        if (ifNoneMatch.Trim() == "*")
        {
            return false; // Resource exists (any ETag), so not modified
        }

        // Parse multiple ETags (comma-separated)
        var etags = ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // If-None-Match uses weak comparison for GET/HEAD.
        var current = ParseETag(currentETag);
        if (etags.Select(ParseETag).Any(candidate => string.Equals(candidate.Value, current.Value, StringComparison.Ordinal)))
        {
            return false; // ETag matches, resource not modified
        }

        return true; // No matching ETag found, resource is modified
    }

    public bool MatchesPrecondition(string? ifMatch, string currentETag)
    {
        if (string.IsNullOrEmpty(ifMatch))
        {
            return true; // No conditional header, precondition passes
        }

        // Handle "*" which means any ETag
        if (ifMatch.Trim() == "*")
        {
            return true; // Resource exists (any ETag), precondition passes
        }

        // Parse multiple ETags (comma-separated)
        var etags = ifMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // If-Match requires strong comparison; weak tags never match.
        var current = ParseETag(currentETag);
        if (!current.IsWeak &&
            etags.Select(ParseETag).Any(candidate => !candidate.IsWeak && string.Equals(candidate.Value, current.Value, StringComparison.Ordinal)))
        {
            return true; // ETag matches, precondition passes
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

    private static (string Value, bool IsWeak) ParseETag(string etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
        {
            return (string.Empty, false);
        }

        var trimmed = etag.Trim();
        var isWeak = false;

        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            isWeak = true;
            trimmed = trimmed[2..].TrimStart();
        }

        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1];
        }

        return (trimmed, isWeak);
    }
}
