// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Infrastructure.Helpers;

// Pooled StringBuilders for large per-request text documents (capabilities XML). A rented builder keeps its
// storage between requests instead of growing a new builder through every chunk size, and its text is encoded
// straight into a pooled UTF-8 buffer, so the document is never copied into a string.
internal static class PooledTextBuilder
{
    private const int InitialCapacity = 16 * 1024;

    // Builders that grew past this many characters are dropped rather than retained by the pool.
    private const int MaximumRetainedCapacity = 512 * 1024;

    private static readonly ObjectPool<StringBuilder> Pool =
        new DefaultObjectPoolProvider().CreateStringBuilderPool(InitialCapacity, MaximumRetainedCapacity);

    internal static StringBuilder Rent() => Pool.Get();

    internal static void Return(StringBuilder builder) => Pool.Return(builder);

    // Same bytes and headers as Results.Content(builder.ToString(), contentType): contentType is sent as given,
    // so pass PooledUtf8ContentResult.WithUtf8Charset(mediaType) where Results.Content received Encoding.UTF8.
    internal static PooledUtf8ContentResult ToUtf8Result(StringBuilder builder, string contentType)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var buffer = new PooledByteBuffer(Math.Max(builder.Length, 1));
        try
        {
            EncodeUtf8(builder, buffer);
            return new PooledUtf8ContentResult(buffer, contentType);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    // One encoder across chunks, so a surrogate pair split between two chunks encodes as one code point and a
    // lone surrogate becomes U+FFFD, exactly as Encoding.UTF8.GetBytes(builder.ToString()) would encode it.
    internal static void EncodeUtf8(StringBuilder builder, PooledByteBuffer destination)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        foreach (var chunk in builder.GetChunks())
        {
            var chars = chunk.Span;
            while (!chars.IsEmpty)
            {
                var span = destination.GetSpan(Encoding.UTF8.GetMaxByteCount(Math.Min(chars.Length, 4096)));
                encoder.Convert(chars, span, flush: false, out var charsUsed, out var bytesUsed, out _);
                destination.Advance(bytesUsed);
                chars = chars[charsUsed..];
            }
        }

        var tail = destination.GetSpan(Encoding.UTF8.GetMaxByteCount(1));
        encoder.Convert(ReadOnlySpan<char>.Empty, tail, flush: true, out _, out var tailBytes, out _);
        destination.Advance(tailBytes);
    }
}
