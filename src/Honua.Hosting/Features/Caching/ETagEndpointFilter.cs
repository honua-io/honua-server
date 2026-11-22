// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

internal sealed partial class ETagEndpointFilter : IEndpointFilter
{
    private readonly ILogger<ETagEndpointFilter> _logger;
    private readonly JsonSerializerOptions _serializerOptions;

    public ETagEndpointFilter(
        ILogger<ETagEndpointFilter> logger,
        IOptions<JsonOptions> jsonOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serializerOptions = jsonOptions?.Value?.SerializerOptions
            ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

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

            etag = await ConfigurationResponseETag.ForResponseAsync(httpContext, etag,
                (result as IContentTypeHttpResult)?.ContentType ?? "application/json").ConfigureAwait(false);

            var preconditionResult = EvaluatePreconditions(httpContext, etag, etagService);
            if (preconditionResult is not null)
            {
                return preconditionResult;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.ETagGenerated(_logger, httpContext.Request.Path.Value ?? string.Empty, etag);
            }

            return new ResultWithETag(result, etag, etagService);
        }

        return result;
    }

    private async ValueTask<object> HandleObjectWithETag(HttpContext httpContext, object result, IETagService etagService)
    {
        var etag = ComputeETagForValueResult(result, etagService, _serializerOptions);
        if (etag == null)
        {
            return result;
        }

        etag = await ConfigurationResponseETag.ForResponseAsync(httpContext, etag, "application/json").ConfigureAwait(false);

        var preconditionResult = EvaluatePreconditions(httpContext, etag, etagService);
        if (preconditionResult is not null)
        {
            return preconditionResult;
        }

        etagService.SetCacheHeaders(httpContext.Response, etag);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.ETagGenerated(_logger, httpContext.Request.Path.Value ?? string.Empty, etag);
        }

        return result;
    }

    private IResult? EvaluatePreconditions(HttpContext httpContext, string etag, IETagService etagService)
    {
        var headers = httpContext.Request.Headers;
        if (headers.TryGetValue("If-Match", out var ifMatchValues) && ifMatchValues.Count > 0)
        {
            var ifMatch = ifMatchValues.ToString();
            if (!string.IsNullOrEmpty(ifMatch) && !etagService.MatchesPrecondition(ifMatch, etag))
            {
                Log.PreconditionFailed(_logger, httpContext.Request.Path.Value ?? string.Empty, ifMatch, etag);
                return Results.StatusCode((int)HttpStatusCode.PreconditionFailed);
            }
        }

        if (headers.TryGetValue("If-None-Match", out var ifNoneMatchValues) && ifNoneMatchValues.Count > 0)
        {
            var ifNoneMatch = ifNoneMatchValues.ToString();
            if (!etagService.IsModified(ifNoneMatch, etag))
            {
                Log.NotModified(_logger, httpContext.Request.Path.Value ?? string.Empty, ifNoneMatch, etag);
                return new NotModifiedWithETagResult(etag, etagService);
            }
        }

        return null;
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

            if (!serializerOptions.TryGetTypeInfo(value.GetType(), out var typeInfo))
            {
                return null;
            }

            using var payload = ETagJsonPayload.Serialize(value, typeInfo);
            return etagService.ComputeETag(payload.WrittenSpan);
        }
        catch (NotSupportedException)
        {
            // Type info not available in AOT-friendly serializer options.
            return null;
        }
        // Intentional: ETag generation is a caching optimization, not a required
        // behavior — any serialization failure is logged and falls back to
        // skipping the ETag header rather than failing the response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

        public Task ExecuteAsync(HttpContext httpContext)
        {
            _etagService.SetCacheHeaders(httpContext.Response, _etag);

            return _originalResult.ExecuteAsync(httpContext);
        }
    }

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
