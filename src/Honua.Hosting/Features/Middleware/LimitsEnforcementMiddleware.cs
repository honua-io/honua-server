// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using InfrastructureLog = Honua.Infrastructure.Logging.ProtocolRequestLog;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Middleware to enforce resource limits across all protocols (GeoServices REST, OGC API Features, OData, MVT).
/// Provides early validation of request constraints to prevent resource exhaustion and ensure consistent behavior.
/// </summary>
/// <remarks>
/// This middleware enforces limits before requests reach handlers:
/// - Request payload size validation
/// - Request timeout configuration
/// - Connection concurrency tracking (future enhancement)
/// - Early validation for known limit violations
///
/// Per-endpoint limits (MaxRecordCount, spatial bounds) are enforced in individual handlers
/// using the injected LimitsOptions configuration.
/// </remarks>
internal sealed class LimitsEnforcementMiddleware(
    RequestDelegate next,
    ILogger<LimitsEnforcementMiddleware> logger,
    IOptions<LimitsOptions> limitsOptions)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<LimitsEnforcementMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly LimitsOptions _limits = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Apply request body size limits early (covers chunked uploads)
        var hasBody = HasRequestBody(context);
        var maxPayloadSize = ResolveMaxPayloadSize(context);
        if (hasBody)
        {
            ApplyRequestBodyLimit(context, maxPayloadSize);
        }

        // 2. Validate request payload size early when Content-Length is present
        if (hasBody && !ValidatePayloadSize(context, maxPayloadSize))
        {
            await WriteErrorResponseAsync(context, 413, "Request payload exceeds maximum allowed size",
                $"Maximum payload size is {maxPayloadSize:N0} bytes");
            return;
        }

        // 3. Set up request timeout cancellation token
        using var timeoutCts = new CancellationTokenSource(_limits.Connections.RequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeoutCts.Token);

        // Make the timeout token available to downstream handlers
        context.Items["LimitsTimeoutToken"] = linkedCts.Token;

        // 4. Log request processing start with limits context
        var requestPath = context.Request.Path.Value ?? string.Empty;
        InfrastructureLog.RequestProcessingStarted(_logger, context.Request.Method, requestPath,
            context.Request.ContentLength ?? 0, _limits.Connections.RequestTimeout.TotalSeconds);

        try
        {
            // Continue to next middleware
            await _next(context);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Request timed out
            InfrastructureLog.RequestTimedOut(_logger, requestPath, _limits.Connections.RequestTimeout.TotalSeconds);

            if (context.Response.HasStarted)
            {
                // The status line and part of a streamed body are already on the wire, so a 408 can
                // no longer be sent. Returning normally would let the server close the response with
                // valid framing and the client would read the truncated body as a complete success;
                // abort so the HTTP/1.1 chunked body or HTTP/2 stream terminates abnormally (#5472).
                context.Abort();
                return;
            }

            await WriteErrorResponseAsync(context, 408, "Request timeout",
                $"Request exceeded maximum allowed time of {_limits.Connections.RequestTimeout.TotalSeconds} seconds");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; no response to write.
        }
        catch (Exception ex)
        {
            // Log unexpected errors (but don't handle them - let other middleware handle)
            InfrastructureLog.RequestProcessingError(_logger, requestPath, ex.GetType().Name, ex.Message, ex);
            throw;
        }
        // linkedCts/timeoutCts are already disposed by their 'using' declarations above at method
        // exit; no manual disposal is needed here.
    }

    /// <summary>
    /// Checks if the request has a body that needs size validation.
    /// </summary>
    private static bool HasRequestBody(HttpContext context)
    {
        string method = context.Request.Method;
        return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that the request payload size doesn't exceed configured limits.
    /// </summary>
    private bool ValidatePayloadSize(HttpContext context, long maxPayloadSize)
    {
        long? contentLength = context.Request.ContentLength;

        // If content length is not provided, we'll let it proceed and rely on
        // the request body size limits configured at the Kestrel level
        if (!contentLength.HasValue)
        {
            var requestPath = context.Request.Path.Value ?? string.Empty;
            InfrastructureLog.PayloadSizeValidationSkipped(_logger, requestPath);
            return true;
        }

        if (contentLength.Value > maxPayloadSize)
        {
            var requestPath = context.Request.Path.Value ?? string.Empty;
            InfrastructureLog.PayloadSizeExceeded(_logger, requestPath, contentLength.Value, maxPayloadSize);
            return false;
        }

        return true;
    }

    private long ResolveMaxPayloadSize(HttpContext context)
    {
        // Implicit routing has already run, so a missing endpoint is a path that
        // only looks like an import (for example /x/admin/import/upload). Those
        // requests stay on the edit ceiling instead of the 500 MB import grant.
        if (context.GetEndpoint() is null)
        {
            return _limits.Edits.MaxPayloadSize;
        }

        var classified = ImportPayloadSize.Resolve(context.Request.Path.Value, _limits);
        if (classified <= _limits.Edits.MaxPayloadSize)
        {
            return classified;
        }

        // Urlencoded bodies are fully buffered by form parsing. Only a real
        // multipart upload on a matched import route may use the larger ceiling.
        // Anonymous callers are clamped back to the edit ceiling after authentication.
        return HasMultipartContentType(context.Request.ContentType)
            ? classified
            : _limits.Edits.MaxPayloadSize;
    }

    private static bool HasMultipartContentType(string? contentType)
        => !string.IsNullOrEmpty(contentType)
           && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

    private static void ApplyRequestBodyLimit(HttpContext context, long maxPayloadSize)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature == null || feature.IsReadOnly)
        {
            return;
        }

        feature.MaxRequestBodySize = maxPayloadSize;
    }

    /// <summary>
    /// Writes a standardized error response for limit violations.
    /// </summary>
    private static Task WriteErrorResponseAsync(HttpContext context, int statusCode, string error, string details)
    {
        var errorResponse = new StandardErrorResponse(statusCode, error, details);
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        return result.ExecuteAsync(context);
    }
}

/// <summary>
/// Extension methods for registering LimitsEnforcementMiddleware.
/// </summary>
public static class LimitsEnforcementMiddlewareExtensions
{
    /// <summary>
    /// Adds limits enforcement middleware to the application pipeline.
    /// Should be registered early in the pipeline, but after correlation ID middleware.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseLimitsEnforcement(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<LimitsEnforcementMiddleware>();
    }

    /// <summary>
    /// After portal-token authentication, drops an anonymous caller off an
    /// import-scale body ceiling. Authenticated multipart uploads keep the
    /// limit applied by <see cref="UseLimitsEnforcement"/>.
    /// </summary>
    public static IApplicationBuilder UseAnonymousImportPayloadClamp(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AnonymousImportPayloadClampMiddleware>();
    }
}

/// <summary>
/// Rejects anonymous requests that would otherwise inherit an import-scale body limit.
/// </summary>
internal sealed class AnonymousImportPayloadClampMiddleware(
    RequestDelegate next,
    IOptions<LimitsOptions> limitsOptions)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly LimitsOptions _limits = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var classified = ImportPayloadSize.Resolve(context.Request.Path.Value, _limits);
        if (!IsImportScale(classified))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var editCeiling = _limits.Edits.MaxPayloadSize;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = editCeiling;
        }

        if (context.Request.ContentLength is long contentLength && contentLength > editCeiling)
        {
            var errorResponse = new StandardErrorResponse(
                StatusCodes.Status413PayloadTooLarge,
                "Request payload exceeds maximum allowed size",
                $"Maximum payload size is {editCeiling:N0} bytes");
            await StandardErrorResponseFormatter.FormatError(context, errorResponse).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool IsImportScale(long classified)
        => classified > _limits.Edits.MaxPayloadSize
           && (classified == _limits.Imports.MaxImportSize
               || classified == _limits.Imports.MaxTilePackageSize
               || classified == _limits.Imports.MaxPreviewSize
               || classified == _limits.Imports.MaxSyncImportSize);
}
