// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;
using InfrastructureLog = Honua.Infrastructure.Logging.ProtocolRequestLog;

namespace Honua.Infrastructure.Middleware;

internal sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger,
    IHostEnvironment environment,
    IOptions<LimitsOptions> limitsOptions)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<GlobalExceptionMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly bool _includeDebugDetails = environment?.IsDevelopment() ?? false;
    private readonly IOptions<LimitsOptions> _limitsOptions = limitsOptions ?? throw new ArgumentNullException(nameof(limitsOptions));

    // WEEK 5 FIX: Fast-path optimization components

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; skip logging and response shaping.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: this is the outermost
            // global exception middleware for the whole pipeline; any unhandled exception
            // from downstream middleware/endpoints is logged and mapped to a
            // protocol-appropriate error response below.
            // WEEK 5 FIX: Fast-path exception handling with categorization
            await HandleExceptionFastPath(context, ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task HandleExceptionFastPath(HttpContext context, Exception ex)
    {
        // Check if response has already been started - fast check first
        if (context.Response.HasStarted)
        {
            // Cannot modify response after it has started - rethrow to let framework handle
            ExceptionDispatchInfo.Capture(ex).Throw();
            return;
        }

        // WEEK 5 FIX: Categorize exception for fast-path handling
        var category = ExceptionCategorizer.CategorizeException(ex);

        // WEEK 5 FIX: Fast-path for common exceptions (avoid full logging and processing)
        if (category.IsFastPath)
        {
            await HandleFastPathException(context, ex, category);
            return;
        }

        // Log the unhandled exception with correlation ID (only for non-fast-path)
        InfrastructureLog.UnhandledException(_logger, context.Request.Path, context.TraceIdentifier, ex.Message, ex);

        // Convert to standardized error response based on protocol
        await HandleExceptionAsync(context, ex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task HandleFastPathException(HttpContext context, Exception ex, ExceptionCategory category)
    {
        if (category.Type == ExceptionType.Timeout)
        {
            InfrastructureLog.RequestTimedOut(
                _logger,
                context.Request.Path.Value ?? string.Empty,
                _limitsOptions.Value.Connections.RequestTimeout.TotalSeconds);
        }

        // An exception that reaches this middleware is unhandled by definition and may
        // indicate a server bug (e.g. an internal ArgumentNullException surfacing as a
        // 400), so fast-path exceptions must still leave a log/correlation trail. Log
        // at Information so common client-triggered validation noise stays filterable
        // while keeping the exception type, path, and trace identifier diagnosable.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var exceptionType = ex.GetType().Name;
            string requestPath = context.Request.Path;
            GlobalExceptionLog.FastPathExceptionHandled(
                _logger,
                exceptionType,
                requestPath,
                context.TraceIdentifier,
                ex.Message,
                ex);
        }

        // Preserve protocol-specific error contracts even on the fast path.
        await HandleExceptionAsync(context, ex);
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Add correlation ID header for traceability
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;

        if (exception is CapabilityUnavailableException capabilityException)
        {
            await ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                    context,
                    capabilityException.Message,
                    capabilityException.MissingDependency,
                    capabilityException.Remediation,
                    capabilityException.RemediationRef,
                    capabilityException.Capability)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        // Use standardized error handling system
        var errorResponse = StandardErrorResponse.FromException(exception, _includeDebugDetails);

        // Prepare formatter options with debug info if enabled
        var options = new ErrorResponseFormatterOptions
        {
            IncludeDebugInfo = _includeDebugDetails
        };

        // Temporary unavailability is retryable even without a delay hint. Keep
        // metadata consistent when authentication fails before an endpoint runs.
        if (exception is ServiceUnavailableException serviceEx)
        {
            var headers = new Dictionary<string, string>
            {
                ["Honua-Retryable"] = "true"
            };
            if (serviceEx.RetryAfterSeconds.HasValue)
            {
                headers["Retry-After"] = serviceEx.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            options = new ErrorResponseFormatterOptions
            {
                IncludeAdditionalDetails = options.IncludeAdditionalDetails,
                IncludeDebugInfo = options.IncludeDebugInfo,
                ContentType = options.ContentType,
                AdditionalHeaders = headers,
                MachineCode = BackpressureMetadata.ServiceUnavailableCode,
                Retryable = true,
                RetryAfterSeconds = serviceEx.RetryAfterSeconds
            };
        }

        // Use standardized error formatter for protocol-appropriate response
        await StandardErrorResponseFormatter.WriteErrorAsync(context, errorResponse, options);
    }
}

internal static partial class GlobalExceptionLog
{
    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Information,
        Message = "Fast-path exception {ExceptionType} handled in {RequestPath} [CorrelationId: {CorrelationId}]: {ErrorMessage}")]
    public static partial void FastPathExceptionHandled(
        ILogger logger,
        string exceptionType,
        string requestPath,
        string correlationId,
        string errorMessage,
        Exception exception);
}

internal static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<GlobalExceptionMiddleware>();
    }
}

// WEEK 5 FIX: Fast-path optimization components

internal sealed class ExceptionCategorizer
{
    private static readonly ConcurrentDictionary<Type, ExceptionCategory> _categoryCache = new();

    public static ExceptionCategory CategorizeException(Exception exception)
    {
        var exceptionType = exception.GetType();

        return _categoryCache.GetOrAdd(exceptionType, type => CreateCategory(type, exception));
    }

    private static ExceptionCategory CreateResourceAccessDeniedCategory()
        => new()
        {
            Type = ExceptionType.Authorization,
            StatusCode = 403,
            Title = "Forbidden",
            Message = "The operation is not permitted for this resource.",
            IsFastPath = true
        };

    private static ExceptionCategory CreateCategory(Type exceptionType, Exception exception)
    {
        // Fast-path exceptions (common, lightweight processing)
        if (exceptionType == typeof(ArgumentNullException))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.ValidationError,
                StatusCode = 400,
                Title = "Bad Request",
                Message = "Invalid request parameters",
                IsFastPath = true
            };
        }

        if (exceptionType == typeof(ArgumentException))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.ValidationError,
                StatusCode = 400,
                Title = "Bad Request",
                Message = "Invalid request format",
                IsFastPath = true
            };
        }

        if (exceptionType == typeof(FileNotFoundException))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.NotFound,
                StatusCode = 404,
                Title = "Not Found",
                Message = "Resource not found",
                IsFastPath = true
            };
        }

        if (typeof(ResourceAccessDeniedException).IsAssignableFrom(exceptionType))
        {
            return CreateResourceAccessDeniedCategory();
        }

        if (exceptionType == typeof(UnauthorizedAccessException))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.Authorization,
                StatusCode = 401,
                Title = "Unauthorized",
                Message = "Authentication is required to access this resource.",
                IsFastPath = true
            };
        }

        if (exceptionType == typeof(TimeoutException) || typeof(OperationCanceledException).IsAssignableFrom(exceptionType))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.Timeout,
                StatusCode = 408,
                Title = "Request Timeout",
                Message = "Request timeout",
                IsFastPath = true
            };
        }

        // Custom exception handling - check by name to avoid direct type dependency
        if (exceptionType.Name.Contains("Validation"))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.ValidationError,
                StatusCode = 400,
                Title = "Bad Request",
                Message = "Validation failed",
                IsFastPath = true
            };
        }

        // Handle Honua.Core custom exceptions specifically
        if (exceptionType.Name == "ResourceNotFoundException")
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.NotFound,
                StatusCode = 404,
                Title = "Not Found",
                Message = "The requested resource was not found.",
                IsFastPath = true
            };
        }

        // Check for other custom NotFoundException types
        if (exceptionType.Name.Contains("NotFound"))
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.NotFound,
                StatusCode = 404,
                Title = "Not Found",
                Message = "Resource not found",
                IsFastPath = true
            };
        }

        if (exception is ServiceUnavailableException)
        {
            return new ExceptionCategory
            {
                Type = ExceptionType.ServiceUnavailable,
                StatusCode = 503,
                Title = "Service Unavailable",
                Message = "Service temporarily unavailable",
                IsFastPath = false // Needs special Retry-After header handling
            };
        }

        // Default category for unknown exceptions (full processing required)
        return new ExceptionCategory
        {
            Type = ExceptionType.InternalError,
            StatusCode = 500,
            Title = "Internal Server Error",
            Message = "An internal error occurred",
            IsFastPath = false
        };
    }
}

internal sealed class PrecompiledErrorResponseCache
{
    private static readonly ConcurrentDictionary<string, PrecompiledResponse> _cache = new();

    static PrecompiledErrorResponseCache()
    {
        // Pre-compile common error responses
        PrecompileCommonResponses();
    }

    private static void PrecompileCommonResponses()
    {
        var commonResponses = new[]
        {
            (ExceptionType.ValidationError, 400, "Invalid request parameters"),
            (ExceptionType.NotFound, 404, "The requested resource was not found."),
            (ExceptionType.Authorization, 401, "Authentication is required to access this resource."),
            (ExceptionType.Timeout, 408, "Request timeout"),
            (ExceptionType.InternalError, 500, "An internal error occurred")
        };

        foreach (var (type, statusCode, message) in commonResponses)
        {
            // Compile responses both with and without debug details
            CompileResponse(type, statusCode, message, includeDebug: false);
            CompileResponse(type, statusCode, message, includeDebug: true);
        }
    }

    private static void CompileResponse(ExceptionType type, int statusCode, string message, bool includeDebug)
    {
        var key = $"{type}_{includeDebug}";

        var jsonBuilder = new StringBuilder();

        // Use ProblemDetails format
        jsonBuilder.Append('{');
        jsonBuilder.Append("\"title\":\"").Append(GetTitleForStatusCode(statusCode)).Append("\",");
        jsonBuilder.Append("\"detail\":\"").Append(JsonEncodedText.Encode(message)).Append("\",");
        jsonBuilder.Append("\"status\":").Append(statusCode);

        if (includeDebug)
        {
            jsonBuilder.Append(",\"exception\":{");
            jsonBuilder.Append("\"details\":\"Debug information available\",");
            jsonBuilder.Append("\"type\":\"").Append(type).Append('\"');
            jsonBuilder.Append('}');
        }

        jsonBuilder.Append(",\"traceId\":\"{correlationId}\"");
        jsonBuilder.Append('}');

        var response = new PrecompiledResponse
        {
            StatusCode = statusCode,
            ContentType = "application/problem+json; charset=utf-8",
            JsonTemplate = jsonBuilder.ToString(),
            BodyBytes = Encoding.UTF8.GetBytes(jsonBuilder.ToString())
        };

        _cache.TryAdd(key, response);
    }

    private static string GetTitleForStatusCode(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        408 => "Request Timeout",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Error"
    };
}

// WEEK 5 FIX: Supporting data structures

internal enum ExceptionType
{
    ValidationError,
    NotFound,
    Authorization,
    Timeout,
    ServiceUnavailable,
    InternalError
}

internal sealed class ExceptionCategory
{
    public ExceptionType Type { get; set; }
    public int StatusCode { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsFastPath { get; set; }
}

internal sealed class PrecompiledResponse
{
    public int StatusCode { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string JsonTemplate { get; set; } = string.Empty;
    public byte[] BodyBytes { get; set; } = Array.Empty<byte>();
}
