// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Grpc.Core.Interceptors;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// Maps unhandled server exceptions to gRPC status codes with safe client-facing messages.
/// </summary>
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
        catch (Exception exception)
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
        catch (Exception exception)
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
        catch (Exception exception)
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
        catch (Exception exception)
        {
            throw CreateMappedRpcException(exception, context);
        }
    }

    private RpcException CreateMappedRpcException(Exception exception, ServerCallContext context)
    {
        var mapping = ExceptionMapper.Map(exception);
        var grpcStatusCode = MapStatusCode(mapping.StatusCode, exception, context.CancellationToken);

        if (mapping.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            GrpcExceptionInterceptorLog.LogServerExceptionMapped(
                _logger,
                context.Method,
                grpcStatusCode,
                mapping.StatusCode,
                exception);
        }
        else
        {
            GrpcExceptionInterceptorLog.LogClientExceptionMapped(
                _logger,
                context.Method,
                grpcStatusCode,
                mapping.StatusCode,
                exception);
        }

        return new RpcException(new Status(grpcStatusCode, mapping.Detail));
    }

    private static StatusCode MapStatusCode(int httpStatusCode, Exception exception, CancellationToken cancellationToken)
    {
        return httpStatusCode switch
        {
            StatusCodes.Status400BadRequest => StatusCode.InvalidArgument,
            StatusCodes.Status401Unauthorized => StatusCode.Unauthenticated,
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
        Level = LogLevel.Warning,
        Message = "Mapped gRPC client exception for {Method} to {GrpcStatusCode} from HTTP status {HttpStatusCode}.")]
    public static partial void LogClientExceptionMapped(
        ILogger logger,
        string method,
        StatusCode grpcStatusCode,
        int httpStatusCode,
        Exception exception);

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
}
