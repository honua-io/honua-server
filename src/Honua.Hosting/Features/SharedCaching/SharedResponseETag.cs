// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.SharedCaching;

internal static class SharedResponseETag
{
    internal static async ValueTask<object?> ApplyAsync(HttpContext context, object? result)
    {
        if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
            context.Response.HasStarted || context.Response.Headers.ContainsKey(HeaderNames.ETag))
        {
            return result;
        }

        var tags = context.RequestServices.GetService<IETagService>();
        if (tags is null)
        {
            return result;
        }

        if (result is FileContentHttpResult file)
        {
            var etag = await ConfigurationResponseETag.ForResponseAsync(
                context,
                file.EntityTag?.ToString() ?? tags.ComputeETag(file.FileContents.Span),
                file.ContentType ?? "application/octet-stream").ConfigureAwait(false);
            return Results.Bytes(file.FileContents, file.ContentType, file.FileDownloadName,
                file.EnableRangeProcessing, file.LastModified, EntityTagHeaderValue.Parse(etag));
        }

        if (result is CachedUtf8ContentResult or PooledUtf8ContentResult)
        {
            return await ApplyToUtf8ContentAsync(context, result, tags).ConfigureAwait(false);
        }

        if (result is ContentHttpResult { StatusCode: null or StatusCodes.Status200OK } text)
        {
            var etag = await ConfigurationResponseETag.ForResponseAsync(context,
                tags.ComputeETag(text.ResponseContent ?? string.Empty), text.ContentType ?? "text/plain").ConfigureAwait(false);
            return new ConditionalTextResult(text, etag, tags);
        }

        return result;
    }

    private static async ValueTask<object?> ApplyToUtf8ContentAsync(HttpContext context, object result, IETagService tags)
    {
        if (result is CachedUtf8ContentResult cached)
        {
            var rawTag = cached.ContentETag ?? tags.ComputeETag(cached.Content.Span);
            var cachedEtag = await ConfigurationResponseETag.ForResponseAsync(context, rawTag, cached.ContentType).ConfigureAwait(false);
            return new ConditionalTextResult(cached, cachedEtag, tags);
        }

        var pooled = (PooledUtf8ContentResult)result;
        var etag = await ConfigurationResponseETag.ForResponseAsync(context,
            tags.ComputeETag(pooled.Content.Span), pooled.ContentType).ConfigureAwait(false);
        return new ConditionalTextResult(pooled, etag, tags);
    }

    private sealed class ConditionalTextResult(IResult result, string etag, IETagService tags) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            if (!string.IsNullOrEmpty(httpContext.Request.Headers.IfMatch) &&
                !tags.MatchesPrecondition(httpContext.Request.Headers.IfMatch.ToString(), etag))
            {
                httpContext.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                await ReleaseAsync().ConfigureAwait(false);
                return;
            }

            tags.SetCacheHeaders(httpContext.Response, etag);
            if (!tags.IsModified(httpContext.Request.Headers.IfNoneMatch.ToString(), etag))
            {
                httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                await ReleaseAsync().ConfigureAwait(false);
                return;
            }

            await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        }

        private ValueTask ReleaseAsync() => result is PooledUtf8ContentResult pooled ? pooled.ReleaseAsync() : ValueTask.CompletedTask;
    }
}
