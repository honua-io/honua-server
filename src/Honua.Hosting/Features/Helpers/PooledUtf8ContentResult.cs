// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Helpers;

// Writes UTF-8 text already encoded in a PooledByteBuffer with the headers Results.Content(text, mediaType,
// Encoding.UTF8) would set, then returns the buffer to the pool.
internal sealed class PooledUtf8ContentResult : IResult
{
    private readonly PooledByteBuffer _content;

    internal PooledUtf8ContentResult(PooledByteBuffer content, string contentType)
    {
        _content = content;
        ContentType = contentType;
    }

    internal string ContentType { get; }

    internal ReadOnlyMemory<byte> Content => _content.WrittenMemory;

    // The Content-Type value Results.Content builds for this media type and UTF-8.
    internal static string WithUtf8Charset(string mediaType)
    {
        var header = MediaTypeHeaderValue.Parse(mediaType);
        header.Encoding = Encoding.UTF8;
        return header.ToString();
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        try
        {
            var response = httpContext.Response;
            response.ContentType = ContentType;
            response.ContentLength = _content.Length;
            await response.BodyWriter.WriteAsync(_content.WrittenMemory).ConfigureAwait(false);
        }
        finally
        {
            await _content.DisposeAsync().ConfigureAwait(false);
        }
    }
}
