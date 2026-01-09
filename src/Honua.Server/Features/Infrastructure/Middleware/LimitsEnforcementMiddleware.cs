// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using InfrastructureLog = Honua.Server.Features.Infrastructure.Logging.Log;

namespace Honua.Server.Features.Infrastructure.Middleware;

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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeoutCts.CancelAfter(_limits.Connections.RequestTimeout);

        // Make the timeout token available to downstream handlers
        context.Items["LimitsTimeoutToken"] = timeoutCts.Token;

        // 4. Log request processing start with limits context
        InfrastructureLog.RequestProcessingStarted(_logger, context.Request.Method, context.Request.Path,
            context.Request.ContentLength ?? 0, _limits.Connections.RequestTimeout.TotalSeconds);

        try
        {
            // Continue to next middleware
            await _next(context);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            // Request timed out
            InfrastructureLog.RequestTimedOut(_logger, context.Request.Path, _limits.Connections.RequestTimeout.TotalSeconds);

            if (!context.Response.HasStarted)
            {
                await WriteErrorResponseAsync(context, 408, "Request timeout",
                    $"Request exceeded maximum allowed time of {_limits.Connections.RequestTimeout.TotalSeconds} seconds");
            }
        }
        catch (Exception ex)
        {
            // Log unexpected errors (but don't handle them - let other middleware handle)
            InfrastructureLog.RequestProcessingError(_logger, context.Request.Path, ex.GetType().Name, ex.Message, ex);
            throw;
        }
        finally
        {
            // Dispose timeout token source
            timeoutCts.Dispose();
        }
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
            InfrastructureLog.PayloadSizeValidationSkipped(_logger, context.Request.Path);
            return true;
        }

        if (contentLength.Value > maxPayloadSize)
        {
            InfrastructureLog.PayloadSizeExceeded(_logger, context.Request.Path, contentLength.Value, maxPayloadSize);
            return false;
        }

        return true;
    }

    private long ResolveMaxPayloadSize(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (IsImportPreview(path))
        {
            return _limits.Imports.MaxPreviewSize;
        }

        if (IsImportUpload(path))
        {
            return _limits.Imports.MaxImportSize;
        }

        if (IsAttachmentUpload(path))
        {
            return _limits.Attachments.MaxAttachmentSize;
        }

        return _limits.Edits.MaxPayloadSize;
    }

    private static void ApplyRequestBodyLimit(HttpContext context, long maxPayloadSize)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature == null || feature.IsReadOnly)
        {
            return;
        }

        feature.MaxRequestBodySize = maxPayloadSize;
    }

    private static bool IsImportPreview(string path)
        => path.Contains("/admin/import/preview", StringComparison.OrdinalIgnoreCase);

    private static bool IsImportUpload(string path)
        => path.Contains("/admin/import/upload", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttachmentUpload(string path)
        => path.EndsWith("/addAttachment", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith("/updateAttachment", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a standardized error response for limit violations.
    /// </summary>
    private static Task WriteErrorResponseAsync(HttpContext context, int statusCode, string error, string details)
    {
        return ProtocolErrorWriter.WriteErrorAsync(context, statusCode, error, details);
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
}
