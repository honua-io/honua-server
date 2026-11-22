// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Helpers;

internal sealed class CachedUtf8ContentResult : IResult
{
    private readonly ReadOnlyMemory<byte> _content;

    internal CachedUtf8ContentResult(ReadOnlyMemory<byte> content, string contentType, string? contentETag = null)
    {
        _content = content;
        ContentType = contentType;
        ContentETag = contentETag;
    }

    internal string ContentType { get; }

    internal ReadOnlyMemory<byte> Content => _content;

    internal string? ContentETag { get; }

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.ContentType = ContentType;
        response.ContentLength = _content.Length;
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return Task.CompletedTask;
        }

        return response.BodyWriter.WriteAsync(_content).AsTask();
    }
}
