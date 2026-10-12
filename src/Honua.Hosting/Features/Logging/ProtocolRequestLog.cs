// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Logging;

internal static partial class ProtocolRequestLog
{
    [LoggerMessage(
        EventId = 4050,
        Level = LogLevel.Debug,
        Message = "Request processing started: {Method} {Path}, ContentLength={ContentLength}, Timeout={TimeoutSeconds}s")]
    public static partial void RequestProcessingStarted(
        ILogger logger, string method, string path, long contentLength, double timeoutSeconds);

    [LoggerMessage(
        EventId = 4051,
        Level = LogLevel.Warning,
        Message = "Request timed out: {Path} exceeded {TimeoutSeconds}s limit")]
    public static partial void RequestTimedOut(
        ILogger logger, string path, double timeoutSeconds);

    [LoggerMessage(
        EventId = 4052,
        Level = LogLevel.Warning,
        Message = "Payload size exceeded: {Path} has {ActualSize:N0} bytes, limit is {MaxSize:N0} bytes")]
    public static partial void PayloadSizeExceeded(
        ILogger logger, string path, long actualSize, long maxSize);

    [LoggerMessage(
        EventId = 4053,
        Level = LogLevel.Debug,
        Message = "Payload size validation skipped: {Path} (Content-Length header not provided)")]
    public static partial void PayloadSizeValidationSkipped(
        ILogger logger, string path);

    [LoggerMessage(
        EventId = 4054,
        Level = LogLevel.Warning,
        Message = "Request processing error: {Path} failed with {ExceptionType}: {ErrorMessage}")]
    public static partial void RequestProcessingError(
        ILogger logger, string path, string exceptionType, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5005,
        Level = LogLevel.Error,
        Message = "Unhandled exception in {RequestPath} [CorrelationId: {CorrelationId}]: {ErrorMessage}")]
    public static partial void UnhandledException(
        ILogger logger, string requestPath, string correlationId, string errorMessage, Exception exception);
}
