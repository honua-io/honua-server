// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Service for computing and validating ETags for HTTP caching.
/// </summary>
public interface IETagService
{
    /// <summary>
    /// Computes an ETag for a given object by serializing it and hashing the content.
    /// </summary>
    /// <typeparam name="T">The type of object to compute ETag for</typeparam>
    /// <param name="obj">The object to compute ETag for</param>
    /// <param name="jsonTypeInfo">JSON serializer type info for consistent serialization</param>
    /// <returns>A strong ETag value (quoted)</returns>
    string ComputeETag<T>(T obj, JsonTypeInfo<T> jsonTypeInfo);

    /// <summary>
    /// Computes an ETag from a byte array.
    /// </summary>
    /// <param name="data">The data to compute ETag for</param>
    /// <returns>A strong ETag value (quoted)</returns>
    string ComputeETag(ReadOnlySpan<byte> data);

    /// <summary>
    /// Computes an ETag from a string.
    /// </summary>
    /// <param name="content">The string content to compute ETag for</param>
    /// <returns>A strong ETag value (quoted)</returns>
    string ComputeETag(string content);

    /// <summary>
    /// Validates if the If-None-Match header matches the given ETag.
    /// Returns true if the resource should be considered modified (send 200).
    /// Returns false if the resource hasn't been modified (send 304).
    /// </summary>
    /// <param name="ifNoneMatch">The If-None-Match header value</param>
    /// <param name="currentETag">The current ETag of the resource</param>
    /// <returns>True if resource is modified, false if not modified</returns>
    bool IsModified(string? ifNoneMatch, string currentETag);

    /// <summary>
    /// Validates if the If-Match header matches the given ETag.
    /// Returns true if the condition passes.
    /// Returns false if the condition fails (should return 412 Precondition Failed).
    /// </summary>
    /// <param name="ifMatch">The If-Match header value</param>
    /// <param name="currentETag">The current ETag of the resource</param>
    /// <returns>True if condition passes, false if condition fails</returns>
    bool MatchesPrecondition(string? ifMatch, string currentETag);

    /// <summary>
    /// Sets ETag and Last-Modified headers on the response.
    /// </summary>
    /// <param name="response">The HTTP response</param>
    /// <param name="etag">The ETag value</param>
    /// <param name="lastModified">Optional last modified date</param>
    void SetCacheHeaders(HttpResponse response, string etag, DateTimeOffset? lastModified = null);
}
