// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Efficient ETag service using SHA256 for content hashing.
/// Provides strong ETags for better cache validation.
/// </summary>
internal sealed class ETagService : IETagService
{
    private const int HashSize = 32; // SHA256 hash size in bytes
    private const int Base64HashSize = 44; // Base64-encoded SHA256 size (rounded up to nearest 4-byte boundary)

    /// <summary>
    /// Computes an ETag for a given object by serializing it and hashing the content.
    /// </summary>
    /// <typeparam name="T">The type of object to compute ETag for</typeparam>
    /// <param name="obj">The object to compute ETag for</param>
    /// <param name="jsonTypeInfo">JSON serializer type info for consistent serialization</param>
    /// <returns>A strong ETag value (quoted)</returns>
    public string ComputeETag<T>(T obj, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        // Serialize the object to JSON bytes for hashing
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(obj, jsonTypeInfo);

        return ComputeETag(jsonBytes);
    }

    /// <summary>
    /// Computes an ETag from a byte array.
    /// </summary>
    /// <param name="data">The data to compute ETag for</param>
    /// <returns>A strong ETag value (quoted)</returns>
    public string ComputeETag(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            // Return a consistent ETag for empty content
            return "\"d41d8cd98f00b204e9800998ecf8427e\"";
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

    /// <summary>
    /// Computes an ETag from a string.
    /// </summary>
    /// <param name="content">The string content to compute ETag for</param>
    /// <returns>A strong ETag value (quoted)</returns>
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

    /// <summary>
    /// Validates if the If-None-Match header matches the given ETag.
    /// Returns true if the resource should be considered modified (send 200).
    /// Returns false if the resource hasn't been modified (send 304).
    /// </summary>
    /// <param name="ifNoneMatch">The If-None-Match header value</param>
    /// <param name="currentETag">The current ETag of the resource</param>
    /// <returns>True if resource is modified, false if not modified</returns>
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

        foreach (var etag in etags)
        {
            // Normalize ETags for comparison (remove quotes if present)
            var normalizedIfNoneMatch = NormalizeETag(etag);
            var normalizedCurrent = NormalizeETag(currentETag);

            if (string.Equals(normalizedIfNoneMatch, normalizedCurrent, StringComparison.Ordinal))
            {
                return false; // ETag matches, resource not modified
            }
        }

        return true; // No matching ETag found, resource is modified
    }

    /// <summary>
    /// Validates if the If-Match header matches the given ETag.
    /// Returns true if the condition passes.
    /// Returns false if the condition fails (should return 412 Precondition Failed).
    /// </summary>
    /// <param name="ifMatch">The If-Match header value</param>
    /// <param name="currentETag">The current ETag of the resource</param>
    /// <returns>True if condition passes, false if condition fails</returns>
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

        foreach (var etag in etags)
        {
            // Normalize ETags for comparison (remove quotes if present)
            var normalizedIfMatch = NormalizeETag(etag);
            var normalizedCurrent = NormalizeETag(currentETag);

            if (string.Equals(normalizedIfMatch, normalizedCurrent, StringComparison.Ordinal))
            {
                return true; // ETag matches, precondition passes
            }
        }

        return false; // No matching ETag found, precondition fails
    }

    /// <summary>
    /// Sets ETag and Last-Modified headers on the response.
    /// </summary>
    /// <param name="response">The HTTP response</param>
    /// <param name="etag">The ETag value</param>
    /// <param name="lastModified">Optional last modified date</param>
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

    /// <summary>
    /// Normalizes an ETag by removing surrounding quotes and whitespace.
    /// </summary>
    /// <param name="etag">The ETag to normalize</param>
    /// <returns>The normalized ETag without quotes</returns>
    private static string NormalizeETag(string etag)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return string.Empty;
        }

        var trimmed = etag.Trim();

        // Remove quotes if present
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
