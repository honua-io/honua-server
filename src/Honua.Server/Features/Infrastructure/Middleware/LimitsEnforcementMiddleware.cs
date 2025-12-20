// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Logging;
using Microsoft.Extensions.Options;

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
public sealed class LimitsEnforcementMiddleware
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<LimitsEnforcementMiddleware> _logger;
    private readonly LimitsOptions _limits;

    public LimitsEnforcementMiddleware(
        RequestDelegate next,
        ILogger<LimitsEnforcementMiddleware> logger,
        IOptions<LimitsOptions> limitsOptions)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Validate request payload size early
        if (HasRequestBody(context) && !ValidatePayloadSize(context))
        {
            await WriteErrorResponseAsync(context, 413, "Request payload exceeds maximum allowed size",
                $"Maximum payload size is {_limits.Edits.MaxPayloadSize:N0} bytes");
            return;
        }

        // 2. Set up request timeout cancellation token
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeoutCts.CancelAfter(_limits.Connections.RequestTimeout);

        // Make the timeout token available to downstream handlers
        context.Items["LimitsTimeoutToken"] = timeoutCts.Token;

        // 3. Log request processing start with limits context
        Log.RequestProcessingStarted(_logger, context.Request.Method, context.Request.Path,
            context.Request.ContentLength ?? 0, _limits.Connections.RequestTimeout.TotalSeconds);

        try
        {
            // Continue to next middleware
            await _next(context);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            // Request timed out
            Log.RequestTimedOut(_logger, context.Request.Path, _limits.Connections.RequestTimeout.TotalSeconds);

            if (!context.Response.HasStarted)
            {
                await WriteErrorResponseAsync(context, 408, "Request timeout",
                    $"Request exceeded maximum allowed time of {_limits.Connections.RequestTimeout.TotalSeconds} seconds");
            }
        }
        catch (Exception ex)
        {
            // Log unexpected errors (but don't handle them - let other middleware handle)
            Log.RequestProcessingError(_logger, context.Request.Path, ex.GetType().Name, ex.Message, ex);
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
        var method = context.Request.Method;
        return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that the request payload size doesn't exceed configured limits.
    /// </summary>
    private bool ValidatePayloadSize(HttpContext context)
    {
        var contentLength = context.Request.ContentLength;

        // If content length is not provided, we'll let it proceed and rely on
        // the request body size limits configured at the Kestrel level
        if (!contentLength.HasValue)
        {
            Log.PayloadSizeValidationSkipped(_logger, context.Request.Path);
            return true;
        }

        if (contentLength.Value > _limits.Edits.MaxPayloadSize)
        {
            Log.PayloadSizeExceeded(_logger, context.Request.Path, contentLength.Value, _limits.Edits.MaxPayloadSize);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes a standardized error response for limit violations.
    /// </summary>
    private static async Task WriteErrorResponseAsync(HttpContext context, int statusCode, string error, string details)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var errorResponse = new
        {
            error = new
            {
                code = statusCode,
                message = error,
                details = new[] { details }
            }
        };

        var json = JsonSerializer.Serialize(errorResponse, _jsonOptions);

        await context.Response.WriteAsync(json);
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
