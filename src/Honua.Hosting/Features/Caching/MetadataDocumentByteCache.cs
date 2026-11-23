// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Caching;

internal readonly record struct MetadataCacheStamp(long ConfigGeneration, long InvalidationGeneration);

internal sealed class JsonMetadataCacheRequest<T>
{
    internal required string? CacheKey { get; init; }

    internal required JsonTypeInfo<T> TypeInfo { get; init; }

    internal string ContentType { get; init; } = "application/json; charset=utf-8";

    internal required Func<Task<T>> BuildAsync { get; init; }
}

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
    private static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _entryTtl;
    private long _invalidationGeneration;
    private volatile GenerationBucket _bucket = new(0, 0);

    internal MetadataDocumentByteCache()
        : this(TimeProvider.System, DefaultEntryTtl)
    {
    }

    internal MetadataDocumentByteCache(TimeProvider timeProvider, TimeSpan entryTtl)
    {
        _timeProvider = timeProvider;
        _entryTtl = entryTtl;
    }

    internal void Invalidate() => Interlocked.Increment(ref _invalidationGeneration);

    internal MetadataCacheStamp CaptureStamp(long configGeneration) =>
        new(configGeneration, Volatile.Read(ref _invalidationGeneration));

    internal static async ValueTask<long> ResolveConfigGenerationAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var source = context.RequestServices.GetService<IConfigurationGenerationSource>();
        return source is null ? 0 : await source.GetGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    internal CachedMetadataDocument? Get(MetadataCacheStamp stamp, string key)
    {
        if (Volatile.Read(ref _invalidationGeneration) != stamp.InvalidationGeneration)
        {
            return null;
        }

        var bucket = TryGetOrAdvanceBucket(stamp);
        if (bucket is null || !bucket.Entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (_timeProvider.GetElapsedTime(entry.CreatedTicks) <= _entryTtl)
        {
            return entry.Document;
        }

        bucket.Entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        return null;
    }

    internal CachedMetadataDocument Set(MetadataCacheStamp stamp, string key, CachedMetadataDocument document)
    {
        if (Volatile.Read(ref _invalidationGeneration) != stamp.InvalidationGeneration)
        {
            return document;
        }

        var bucket = TryGetOrAdvanceBucket(stamp);
        if (bucket is not null && HasRoom(bucket))
        {
            bucket.Entries[key] = new CacheEntry(document, _timeProvider.GetTimestamp());
        }

        return document;
    }

    private bool HasRoom(GenerationBucket bucket)
    {
        if (bucket.Entries.Count < MaxEntriesPerGeneration)
        {
            return true;
        }

        foreach (var pair in bucket.Entries)
        {
            if (_timeProvider.GetElapsedTime(pair.Value.CreatedTicks) > _entryTtl)
            {
                bucket.Entries.TryRemove(pair);
            }
        }

        return bucket.Entries.Count < MaxEntriesPerGeneration;
    }

    internal static CachedMetadataDocument CreateDocument(ReadOnlySpan<byte> bytes, string contentType, IETagService? etagService)
    {
        var copy = bytes.ToArray();
        var etag = etagService?.ComputeETag(copy);
        return new CachedMetadataDocument(copy, contentType, etag);
    }

    internal static async Task<IResult> GetOrBuildJsonAsync<T>(
        HttpContext context,
        JsonMetadataCacheRequest<T> request)
    {
        var cacheKey = request.CacheKey;
        if (cacheKey is null ||
            HasQueryToken(context.Request) ||
            context.RequestServices.GetService<MetadataDocumentByteCache>() is not { } docCache)
        {
            var uncached = await request.BuildAsync().ConfigureAwait(false);
            return Results.Json(uncached, request.TypeInfo, contentType: request.ContentType);
        }

        var configGeneration = await ResolveConfigGenerationAsync(context, context.RequestAborted)
            .ConfigureAwait(false);
        var stamp = docCache.CaptureStamp(configGeneration);
        if (docCache.Get(stamp, cacheKey) is { } hit)
        {
            return hit.ToResult();
        }

        var payload = await request.BuildAsync().ConfigureAwait(false);
        if (context.Response.GetTypedHeaders().CacheControl?.NoStore == true)
        {
            return Results.Json(payload, request.TypeInfo, contentType: request.ContentType);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, request.TypeInfo);
        var etag = context.RequestServices.GetService<IETagService>()?.ComputeETag(bytes);
        var document = new CachedMetadataDocument(bytes, request.ContentType, etag);
        return docCache.Set(stamp, cacheKey, document).ToResult();
    }

    // Must match the query keys BaseUrlResolver.PreserveToken copies into document links.
    internal static bool HasQueryToken(HttpRequest request) =>
        request.Query.ContainsKey("token");

    internal static string HashKey(StringBuilder keyBuilder)
    {
        using var buffer = new PooledByteBuffer(Math.Max(keyBuilder.Length, 1));
        PooledTextBuilder.EncodeUtf8(keyBuilder, buffer);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer.WrittenSpan, hash);
        return Convert.ToHexString(hash[..16]);
    }

    private GenerationBucket? TryGetOrAdvanceBucket(MetadataCacheStamp stamp)
    {
        while (true)
        {
            var current = _bucket;
            var comparison = CompareStamps(stamp, current.Stamp);
            if (comparison == 0)
            {
                return current;
            }

            if (comparison < 0)
            {
                return null;
            }

            var replacement = new GenerationBucket(stamp.ConfigGeneration, stamp.InvalidationGeneration);
            if (Interlocked.CompareExchange(ref _bucket, replacement, current) == current)
            {
                return replacement;
            }
        }
    }

    private static int CompareStamps(MetadataCacheStamp left, MetadataCacheStamp right)
    {
        var configCompare = left.ConfigGeneration.CompareTo(right.ConfigGeneration);
        return configCompare != 0
            ? configCompare
            : left.InvalidationGeneration.CompareTo(right.InvalidationGeneration);
    }

    private readonly record struct CacheEntry(CachedMetadataDocument Document, long CreatedTicks);

    private sealed class GenerationBucket(long configGeneration, long invalidationGeneration)
    {
        internal MetadataCacheStamp Stamp { get; } = new(configGeneration, invalidationGeneration);

        internal ConcurrentDictionary<string, CacheEntry> Entries { get; } = new(StringComparer.Ordinal);
    }
}
