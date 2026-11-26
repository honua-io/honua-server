// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Grpc.Core.Interceptors;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed class GrpcExceptionInterceptor(ILogger<GrpcExceptionInterceptor> logger) : Interceptor
{
    private readonly ILogger<GrpcExceptionInterceptor> _logger = logger;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        // Intentional broad catch: this is the gRPC request-handling boundary; every unhandled
        // exception from the RPC continuation is logged and mapped to an RpcException status below.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw CreateMappedRpcException(exception, context);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        // Intentional broad catch: this is the gRPC request-handling boundary; every unhandled
        // exception from the RPC continuation is logged and mapped to an RpcException status below.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw CreateMappedRpcException(exception, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        // Intentional broad catch: this is the gRPC request-handling boundary; every unhandled
        // exception from the RPC continuation is logged and mapped to an RpcException status below.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw CreateMappedRpcException(exception, context);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        // Intentional broad catch: this is the gRPC request-handling boundary; every unhandled
        // exception from the RPC continuation is logged and mapped to an RpcException status below.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw CreateMappedRpcException(exception, context);
        }
    }

    private RpcException CreateMappedRpcException(Exception exception, ServerCallContext context)
    {
        if (exception is AttachmentUploadException upload)
        {
            return MapAttachmentUploadException(upload);
        }
        return MapCoreException(exception, context);
    }

    private RpcException MapCoreException(Exception exception, ServerCallContext context)
    {
        var mapping = ExceptionMapper.Map(exception);
        var grpcStatusCode = MapStatusCode(mapping.StatusCode, exception, context.CancellationToken);

        if (grpcStatusCode == StatusCode.Cancelled)
        {
            GrpcExceptionInterceptorLog.LogClientCancelledMapped(
                _logger,
                context.Method,
                grpcStatusCode,
                mapping.StatusCode);
        }
        else if (mapping.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            GrpcExceptionInterceptorLog.LogServerExceptionMapped(
                _logger,
                context.Method,
                grpcStatusCode,
                mapping.StatusCode,
                exception);
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            var exceptionType = exception.GetType().Name;
            GrpcExceptionInterceptorLog.LogClientExceptionMapped(
                _logger,
                context.Method,
                grpcStatusCode,
                mapping.StatusCode,
                exceptionType);
        }

        if (grpcStatusCode is StatusCode.ResourceExhausted or StatusCode.Unavailable)
        {
            var machineCode = grpcStatusCode == StatusCode.ResourceExhausted
                ? BackpressureMetadata.RateLimitExceededCode
                : BackpressureMetadata.ServiceUnavailableCode;
            var retryAfterSeconds = ExceptionMapper.GetRetryAfterSeconds(exception);
            return new RpcException(
                new Status(grpcStatusCode, mapping.Detail),
                BuildBackpressureTrailers(context, machineCode, retryAfterSeconds));
        }

        return new RpcException(new Status(grpcStatusCode, mapping.Detail));
    }

    private static RpcException MapAttachmentUploadException(AttachmentUploadException upload)
    {
        var status = upload.Failure switch
        {
            AttachmentUploadFailure.NotFound or AttachmentUploadFailure.Expired => StatusCode.NotFound,
            AttachmentUploadFailure.Conflict => StatusCode.AlreadyExists,
            AttachmentUploadFailure.InvalidState => StatusCode.FailedPrecondition,
            AttachmentUploadFailure.InvalidContent => StatusCode.InvalidArgument,
            _ => StatusCode.Internal
        };
        return new RpcException(new Status(status, "The attachment operation could not be completed."));
    }

    private static Metadata BuildBackpressureTrailers(
        ServerCallContext context,
        string machineCode,
        int? retryAfterSeconds)
    {
        var trailers = new Metadata
        {
            { BackpressureMetadata.ErrorCodeKey, machineCode },
            { BackpressureMetadata.RetryableKey, "true" },
            { BackpressureMetadata.CorrelationIdKey, ResolveCorrelationId(context) },
        };

        if (retryAfterSeconds.HasValue)
        {
            trailers.Add(
                BackpressureMetadata.RetryAfterKey,
                retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return trailers;
    }

    private static string ResolveCorrelationId(ServerCallContext context)
    {
        var requestValue = context.RequestHeaders
            .FirstOrDefault(entry => string.Equals(entry.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (CorrelationIdMiddleware.TryNormalizeCorrelationId(requestValue, out var correlationId))
        {
            return correlationId;
        }

        try
        {
            var traceIdentifier = context.GetHttpContext().TraceIdentifier;
            if (CorrelationIdMiddleware.TryNormalizeCorrelationId(traceIdentifier, out correlationId))
            {
                return correlationId;
            }
        }
        catch (InvalidOperationException)
        {
            // Unit-hosted ServerCallContext instances do not carry an ASP.NET Core context.
        }

        return Guid.NewGuid().ToString("N");
    }

    private static StatusCode MapStatusCode(int httpStatusCode, Exception exception, CancellationToken cancellationToken)
    {
        return httpStatusCode switch
        {
            StatusCodes.Status400BadRequest => StatusCode.InvalidArgument,
            StatusCodes.Status401Unauthorized => StatusCode.Unauthenticated,
            StatusCodes.Status402PaymentRequired => StatusCode.FailedPrecondition,
            StatusCodes.Status403Forbidden => StatusCode.PermissionDenied,
            StatusCodes.Status404NotFound => StatusCode.NotFound,
            StatusCodes.Status405MethodNotAllowed => StatusCode.Unimplemented,
            StatusCodes.Status408RequestTimeout when exception is OperationCanceledException
                && cancellationToken.IsCancellationRequested => StatusCode.Cancelled,
            StatusCodes.Status408RequestTimeout => StatusCode.DeadlineExceeded,
            StatusCodes.Status409Conflict => StatusCode.Aborted,
            StatusCodes.Status429TooManyRequests => StatusCode.ResourceExhausted,
            StatusCodes.Status503ServiceUnavailable => StatusCode.Unavailable,
            _ => StatusCode.Internal
        };
    }
}

internal static partial class GrpcExceptionInterceptorLog
{
    [LoggerMessage(
        EventId = 5630,
        Level = LogLevel.Information,
        Message = "Mapped gRPC client exception {ExceptionType} for {Method} to {GrpcStatusCode} from HTTP status {HttpStatusCode}.")]
    public static partial void LogClientExceptionMapped(
        ILogger logger,
        string method,
        StatusCode grpcStatusCode,
        int httpStatusCode,
        string exceptionType);

    [LoggerMessage(
        EventId = 5631,
        Level = LogLevel.Error,
        Message = "Mapped gRPC server exception for {Method} to {GrpcStatusCode} from HTTP status {HttpStatusCode}.")]
    public static partial void LogServerExceptionMapped(
        ILogger logger,
        string method,
        StatusCode grpcStatusCode,
        int httpStatusCode,
        Exception exception);

    [LoggerMessage(
        EventId = 5632,
        Level = LogLevel.Debug,
        Message = "Mapped cancelled gRPC call for {Method} to {GrpcStatusCode} from HTTP status {HttpStatusCode}.")]
    public static partial void LogClientCancelledMapped(
        ILogger logger,
        string method,
        StatusCode grpcStatusCode,
        int httpStatusCode);
}
