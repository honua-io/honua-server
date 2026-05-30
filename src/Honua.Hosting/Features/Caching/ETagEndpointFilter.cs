// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Endpoint filter that adds ETag support to specific endpoints.
/// This is more efficient than global middleware for endpoints that need ETags.
/// </summary>
internal sealed partial class ETagEndpointFilter : IEndpointFilter
{
    private readonly ILogger<ETagEndpointFilter> _logger;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Creates a new endpoint filter that computes ETags for JSON responses.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="jsonOptions">JSON serialization options used by the HTTP pipeline</param>
    public ETagEndpointFilter(
        ILogger<ETagEndpointFilter> logger,
        IOptions<JsonOptions> jsonOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serializerOptions = jsonOptions?.Value?.SerializerOptions
            ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        // Only process GET and HEAD requests
        if (!HttpMethods.IsGet(httpContext.Request.Method) && !HttpMethods.IsHead(httpContext.Request.Method))
        {
            return await next(context);
        }

        // Get the ETag service
        var etagService = httpContext.RequestServices.GetService<IETagService>();
        if (etagService == null)
        {
            Log.ETagServiceNotAvailable(_logger, httpContext.Request.Path);
            return await next(context);
        }

        // Execute the endpoint
        var result = await next(context);

        // Handle IResult responses
        if (result is IResult iResult)
        {
            return await HandleIResultWithETag(httpContext, iResult, etagService);
        }

        // Handle direct object responses
        if (result != null)
        {
            return await HandleObjectWithETag(httpContext, result, etagService);
        }

        return result;
    }

    private async ValueTask<IResult> HandleIResultWithETag(HttpContext httpContext, IResult result, IETagService etagService)
    {
        // Check if it's a value result we can process
        if (result is IValueHttpResult valueResult && result is IStatusCodeHttpResult statusResult)
        {
            var statusCode = statusResult.StatusCode ?? StatusCodes.Status200OK;
            if (statusCode != (int)HttpStatusCode.OK)
            {
                return result;
            }

            // Generate ETag from the response value
            var etag = ComputeETagForValueResult(valueResult.Value, etagService, _serializerOptions);
            if (etag == null)
            {
                return result; // Could not compute ETag, return original result
            }

            // Check conditional headers
            var ifNoneMatch = httpContext.Request.Headers["If-None-Match"].ToString();
            var ifMatch = httpContext.Request.Headers["If-Match"].ToString();
            var requestPath = httpContext.Request.Path.Value ?? string.Empty;

            // Validate If-Match header
            if (!string.IsNullOrEmpty(ifMatch) && !etagService.MatchesPrecondition(ifMatch, etag))
            {
                Log.PreconditionFailed(_logger, requestPath, ifMatch, etag);
                return Results.StatusCode((int)HttpStatusCode.PreconditionFailed);
            }

            // Check If-None-Match header
            if (!etagService.IsModified(ifNoneMatch, etag))
            {
                Log.NotModified(_logger, requestPath, ifNoneMatch, etag);

                // Return 304 Not Modified with ETag
                var notModifiedResult = Results.StatusCode((int)HttpStatusCode.NotModified);

                // Set ETag header using a custom result that wraps the 304 response
                return new NotModifiedWithETagResult(etag, etagService);
            }

            Log.ETagGenerated(_logger, requestPath, etag);

            // Return the original result with ETag headers
            return new ResultWithETag(result, etag, etagService);
        }

        return result;
    }

    private ValueTask<object> HandleObjectWithETag(HttpContext httpContext, object result, IETagService etagService)
    {
        var etag = ComputeETagForValueResult(result, etagService, _serializerOptions);
        if (etag == null)
        {
            return ValueTask.FromResult<object>(result);
        }

        var ifNoneMatch = httpContext.Request.Headers["If-None-Match"].ToString();
        var ifMatch = httpContext.Request.Headers["If-Match"].ToString();
        var requestPath = httpContext.Request.Path.Value ?? string.Empty;

        if (!string.IsNullOrEmpty(ifMatch) && !etagService.MatchesPrecondition(ifMatch, etag))
        {
            Log.PreconditionFailed(_logger, requestPath, ifMatch, etag);
            return ValueTask.FromResult<object>(Results.StatusCode((int)HttpStatusCode.PreconditionFailed));
        }

        if (!etagService.IsModified(ifNoneMatch, etag))
        {
            Log.NotModified(_logger, requestPath, ifNoneMatch, etag);
            return ValueTask.FromResult<object>(new NotModifiedWithETagResult(etag, etagService));
        }

        etagService.SetCacheHeaders(httpContext.Response, etag);
        Log.ETagGenerated(_logger, requestPath, etag);
        return ValueTask.FromResult<object>(result);
    }

    private string? ComputeETagForValueResult(
        object? value,
        IETagService etagService,
        JsonSerializerOptions serializerOptions)
    {
        try
        {
            if (value == null)
            {
                return etagService.ComputeETag(string.Empty);
            }

            var typeInfo = serializerOptions.GetTypeInfo(value.GetType());
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            return etagService.ComputeETag(jsonBytes);
        }
        catch (NotSupportedException)
        {
            // Type info not available in AOT-friendly serializer options.
            return null;
        }
        catch (Exception ex)
        {
            Log.ETagComputationFailed(_logger, ex);
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4101, Level = LogLevel.Debug,
            Message = "Generated ETag {ETag} for {Path}")]
        public static partial void ETagGenerated(ILogger logger, string path, string etag);

        [LoggerMessage(EventId = 4102, Level = LogLevel.Debug,
            Message = "Generated ETag for object {ObjectType} at {Path}")]
        public static partial void ETagGeneratedForObject(ILogger logger, string path, string objectType);

        [LoggerMessage(EventId = 4103, Level = LogLevel.Debug,
            Message = "Resource not modified for {Path}, If-None-Match: {IfNoneMatch}, ETag: {ETag}")]
        public static partial void NotModified(ILogger logger, string path, string ifNoneMatch, string etag);

        [LoggerMessage(EventId = 4104, Level = LogLevel.Debug,
            Message = "Precondition failed for {Path}, If-Match: {IfMatch}, ETag: {ETag}")]
        public static partial void PreconditionFailed(ILogger logger, string path, string ifMatch, string etag);

        [LoggerMessage(EventId = 4105, Level = LogLevel.Warning,
            Message = "ETag service not available for {Path}")]
        public static partial void ETagServiceNotAvailable(ILogger logger, string path);

        [LoggerMessage(EventId = 4106, Level = LogLevel.Debug,
            Message = "ETag computation failed, skipping ETag header")]
        public static partial void ETagComputationFailed(ILogger logger, Exception exception);
    }

    /// <summary>
    /// Custom result that wraps a JSON result with ETag headers.
    /// </summary>
    private sealed class ResultWithETag : IResult
    {
        private readonly IResult _originalResult;
        private readonly string _etag;
        private readonly IETagService _etagService;

        public ResultWithETag(IResult originalResult, string etag, IETagService etagService)
        {
            _originalResult = originalResult;
            _etag = etag;
            _etagService = etagService;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            // Set ETag headers before executing the original result
            _etagService.SetCacheHeaders(httpContext.Response, _etag);

            await _originalResult.ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// Custom result for 304 Not Modified responses with ETag.
    /// </summary>
    private sealed class NotModifiedWithETagResult : IResult
    {
        private readonly string _etag;
        private readonly IETagService _etagService;

        public NotModifiedWithETagResult(string etag, IETagService etagService)
        {
            _etag = etag;
            _etagService = etagService;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;

            // Set ETag header for 304 responses
            _etagService.SetCacheHeaders(httpContext.Response, _etag);

            // Remove content headers for 304 responses
            httpContext.Response.Headers.Remove("Content-Type");
            httpContext.Response.ContentLength = 0;

            return Task.CompletedTask;
        }
    }
}
