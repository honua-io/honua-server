// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Caching;

internal sealed class CachedMetadataDocument(byte[] bytes, string contentType, string? contentETag)
{
    internal byte[] Bytes { get; } = bytes;

    internal string ContentType { get; } = contentType;

    internal string? ContentETag { get; } = contentETag;

    internal CachedUtf8ContentResult ToResult() => new(Bytes, ContentType, ContentETag);
}

internal sealed class MetadataDocumentByteCache
{
    private const int MaxEntriesPerGeneration = 256;

    private long _invalidationGeneration;
    private volatile GenerationBucket _bucket = new(0, 0);

    internal void Invalidate() => Interlocked.Increment(ref _invalidationGeneration);

    internal static async ValueTask<long> ResolveConfigGenerationAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var source = context.RequestServices.GetService<IConfigurationGenerationSource>();
        return source is null ? 0 : await source.GetGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    internal CachedMetadataDocument? Get(long configGeneration, string key)
    {
        var bucket = GetCurrentBucket(configGeneration);
        return bucket.Entries.TryGetValue(key, out var document) ? document : null;
    }

    internal CachedMetadataDocument Set(long configGeneration, string key, CachedMetadataDocument document)
    {
        var bucket = GetCurrentBucket(configGeneration);
        if (bucket.Entries.Count < MaxEntriesPerGeneration)
        {
            bucket.Entries[key] = document;
        }

        return document;
    }

    internal static CachedMetadataDocument CreateDocument(ReadOnlySpan<byte> bytes, string contentType, IETagService? etagService)
    {
        var copy = bytes.ToArray();
        var etag = etagService?.ComputeETag(copy);
        return new CachedMetadataDocument(copy, contentType, etag);
    }

    internal static bool HasQueryToken(HttpRequest request) =>
        request.Query.ContainsKey("token");

    internal static string HashKey(StringBuilder keyBuilder)
    {
        using var buffer = new PooledByteBuffer(Encoding.UTF8.GetMaxByteCount(keyBuilder.Length));
        foreach (var chunk in keyBuilder.GetChunks())
        {
            var span = chunk.Span;
            var maxBytes = Encoding.UTF8.GetMaxByteCount(span.Length);
            var written = Encoding.UTF8.GetBytes(span, buffer.GetSpan(maxBytes));
            buffer.Advance(written);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer.WrittenSpan, hash);
        return Convert.ToHexString(hash[..16]);
    }

    private GenerationBucket GetCurrentBucket(long configGeneration)
    {
        var invalidationGeneration = Volatile.Read(ref _invalidationGeneration);
        var current = _bucket;
        if (current.ConfigGeneration == configGeneration && current.InvalidationGeneration == invalidationGeneration)
        {
            return current;
        }

        var replacement = new GenerationBucket(configGeneration, invalidationGeneration);
        var prior = Interlocked.CompareExchange(ref _bucket, replacement, current);
        return prior == current ? replacement : _bucket;
    }

    private sealed class GenerationBucket(long configGeneration, long invalidationGeneration)
    {
        internal long ConfigGeneration { get; } = configGeneration;

        internal long InvalidationGeneration { get; } = invalidationGeneration;

        internal ConcurrentDictionary<string, CachedMetadataDocument> Entries { get; } = new(StringComparer.Ordinal);
    }
}
