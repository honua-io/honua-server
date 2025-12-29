// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Server.Features.Infrastructure.Caching;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Middleware to handle ETag validation for conditional requests.
/// Works in conjunction with ASP.NET Core output caching to provide efficient caching.
/// </summary>
internal sealed partial class ETagMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ETagMiddleware> _logger;

    public ETagMiddleware(RequestDelegate next, ILogger<ETagMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only process GET and HEAD requests
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Skip if the response is already cached by output cache
        if (context.Response.Headers.TryGetValue("X-Cache", out var cacheHeader) &&
            cacheHeader.ToString().Contains("HIT"))
        {
            await _next(context);
            return;
        }

        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        // Store original response headers
        var originalHeaders = new Dictionary<string, string>();

        try
        {
            // Execute the request pipeline
            await _next(context);

            // Only process successful responses
            if (context.Response.StatusCode != (int)HttpStatusCode.OK)
            {
                await CopyResponseToOriginalStream(responseBody, originalBodyStream);
                return;
            }

            // Check if response already has an ETag
            if (context.Response.Headers.ContainsKey("ETag"))
            {
                // ETag already set by the application or output cache
                await CopyResponseToOriginalStream(responseBody, originalBodyStream);
                return;
            }

            // Get the ETag service
            var etagService = context.RequestServices.GetService<IETagService>();
            if (etagService == null)
            {
                await CopyResponseToOriginalStream(responseBody, originalBodyStream);
                return;
            }

            // Generate ETag from response body
            responseBody.Seek(0, SeekOrigin.Begin);
            var responseBytes = responseBody.ToArray();
            var etag = etagService.ComputeETag(responseBytes);

            // Check conditional headers
            var ifNoneMatch = context.Request.Headers["If-None-Match"].ToString();
            var ifMatch = context.Request.Headers["If-Match"].ToString();

            // Validate If-Match header (used for updates)
            if (!string.IsNullOrEmpty(ifMatch) && !etagService.MatchesPrecondition(ifMatch, etag))
            {
                Log.PreconditionFailed(_logger, context.Request.Path, ifMatch, etag);
                context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                context.Response.ContentLength = 0;
                return;
            }

            // Check If-None-Match header (used for conditional GETs)
            if (!etagService.IsModified(ifNoneMatch, etag))
            {
                Log.NotModified(_logger, context.Request.Path, ifNoneMatch, etag);

                // Set status to 304 Not Modified
                context.Response.StatusCode = (int)HttpStatusCode.NotModified;
                context.Response.ContentLength = 0;

                // Set ETag header for 304 responses
                etagService.SetCacheHeaders(context.Response, etag);

                // Remove content headers for 304 responses
                context.Response.Headers.Remove("Content-Type");
                context.Response.Headers.Remove("Content-Length");

                return;
            }

            // Set ETag headers for successful response
            etagService.SetCacheHeaders(context.Response, etag);

            Log.ETagGenerated(_logger, context.Request.Path, etag);

            // Copy the response to the original stream
            await CopyResponseToOriginalStream(responseBody, originalBodyStream);
        }
        catch (Exception ex)
        {
            Log.ETagMiddlewareError(_logger, context.Request.Path, ex);

            // Restore original response body and continue
            context.Response.Body = originalBodyStream;
            throw;
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private static async Task CopyResponseToOriginalStream(MemoryStream responseBody, Stream originalBodyStream)
    {
        responseBody.Seek(0, SeekOrigin.Begin);
        await responseBody.CopyToAsync(originalBodyStream);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4001, Level = LogLevel.Debug,
            Message = "Generated ETag {ETag} for {Path}")]
        public static partial void ETagGenerated(ILogger logger, string path, string etag);

        [LoggerMessage(EventId = 4002, Level = LogLevel.Debug,
            Message = "Resource not modified for {Path}, If-None-Match: {IfNoneMatch}, ETag: {ETag}")]
        public static partial void NotModified(ILogger logger, string path, string ifNoneMatch, string etag);

        [LoggerMessage(EventId = 4003, Level = LogLevel.Debug,
            Message = "Precondition failed for {Path}, If-Match: {IfMatch}, ETag: {ETag}")]
        public static partial void PreconditionFailed(ILogger logger, string path, string ifMatch, string etag);

        [LoggerMessage(EventId = 4004, Level = LogLevel.Warning,
            Message = "ETag middleware error for {Path}")]
        public static partial void ETagMiddlewareError(ILogger logger, string path, Exception exception);
    }
}
