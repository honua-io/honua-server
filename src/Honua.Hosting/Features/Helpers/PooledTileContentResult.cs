// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Helpers;

internal sealed class PooledTileContentResult : IResult
{
    private readonly PooledTileData _tile;

    internal PooledTileContentResult(PooledTileData tile, string contentType)
    {
        _tile = tile;
        ContentType = contentType;
    }

    internal string ContentType { get; }

    internal ReadOnlyMemory<byte> Content => _tile.Memory;

    internal void Release() => _tile.Dispose();

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        try
        {
            var response = httpContext.Response;
            response.ContentType = ContentType;
            response.ContentLength = _tile.Length;
            if (!HttpMethods.IsHead(httpContext.Request.Method))
            {
                await response.BodyWriter.WriteAsync(_tile.Memory).ConfigureAwait(false);
            }
        }
        finally
        {
            _tile.Dispose();
        }
    }
}
