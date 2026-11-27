// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed class GrpcExceptionInterceptorTests
{
    [UnitTest]
    public async Task UnaryServerHandler_MapsResourceNotFoundException_ToNotFoundAndLogsInformationWithoutException()
    {
        var logger = new ListLogger<GrpcExceptionInterceptor>();
        var interceptor = new GrpcExceptionInterceptor(logger);
        using var context = new TestServerCallContext();

        var act = async () => await interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) => throw new ResourceNotFoundException("missing"));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
        exception.Which.Status.Detail.Should().Be("The requested resource was not found.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].LogLevel.Should().Be(LogLevel.Information);
        logger.Entries[0].EventId.Id.Should().Be(5630);
        logger.Entries[0].Exception.Should().BeNull();
    }

    [UnitTest]
    public async Task UnaryServerHandler_MapsClientCancelled_ToCancelledAndLogsDebugWithoutException()
    {
        var logger = new ListLogger<GrpcExceptionInterceptor>();
        var interceptor = new GrpcExceptionInterceptor(logger);
        using var context = new TestServerCallContext();
        context.Cancel();

        var act = async () => await interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, callContext) => throw new OperationCanceledException(callContext.CancellationToken));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.Cancelled);
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].LogLevel.Should().Be(LogLevel.Debug);
        logger.Entries[0].EventId.Id.Should().Be(5632);
        logger.Entries[0].Exception.Should().BeNull();
    }

    [UnitTest]
    public async Task ServerStreamingServerHandler_PreservesValidInboundCorrelationId()
    {
        const string validCorrelationId = "valid-corr_id.123:abc";

        var emittedCorrelationId = await InvokeUnavailableWithCorrelationIdAsync(validCorrelationId);

        emittedCorrelationId.Should().Be(validCorrelationId);
    }

    [UnitTheory]
    [InlineData("bad\r\ninjected")]
    [InlineData("bad with spaces")]
    [InlineData("bad\"quote")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ServerStreamingServerHandler_ReplacesUnsafeInboundCorrelationId(string unsafeCorrelationId)
    {
        var emittedCorrelationId = await InvokeUnavailableWithCorrelationIdAsync(unsafeCorrelationId);

        emittedCorrelationId.Should().NotBe(unsafeCorrelationId);
        emittedCorrelationId.Should().MatchRegex("^[a-f0-9]{32}$");
    }

    private static async Task<string> InvokeUnavailableWithCorrelationIdAsync(string inboundCorrelationId)
    {
        var interceptor = new GrpcExceptionInterceptor(new ListLogger<GrpcExceptionInterceptor>());
        var headers = new Metadata { { "x-correlation-id", inboundCorrelationId } };
        using var context = new TestServerCallContext(headers);
        var responseStream = new TestServerStreamWriter<string>();

        var act = async () => await interceptor.ServerStreamingServerHandler(
            "request",
            responseStream,
            context,
            (_, _, _) => throw new ServiceUnavailableException("downstream unavailable", 10));

        var exception = await act.Should().ThrowAsync<RpcException>();
        return exception.Which.Trailers
            .Single(entry => entry.Key == BackpressureMetadata.CorrelationIdKey).Value;
    }

    [UnitTest]
    public async Task UnaryServerHandler_MapsValidationException_ToInvalidArgument()
    {
        var interceptor = new GrpcExceptionInterceptor(new ListLogger<GrpcExceptionInterceptor>());
        using var context = new TestServerCallContext();

        var act = async () => await interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) => throw new ValidationException("bbox is invalid"));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        exception.Which.Status.Detail.Should().Be("bbox is invalid");
    }

    [UnitTest]
    public async Task UnaryServerHandler_MapsUnhandledException_ToInternalAndLogsError()
    {
        var logger = new ListLogger<GrpcExceptionInterceptor>();
        var interceptor = new GrpcExceptionInterceptor(logger);
        using var context = new TestServerCallContext();

        var act = async () => await interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) => throw new InvalidOperationException("boom"));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.Internal);
        exception.Which.Status.Detail.Should().Be("An unexpected error occurred while processing the request.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].LogLevel.Should().Be(LogLevel.Error);
        logger.Entries[0].EventId.Id.Should().Be(5631);
    }

    [UnitTest]
    public async Task UnaryServerHandler_PreservesRpcException()
    {
        var interceptor = new GrpcExceptionInterceptor(new ListLogger<GrpcExceptionInterceptor>());
        using var context = new TestServerCallContext();
        var expected = new RpcException(new Status(StatusCode.PermissionDenied, "denied"));

        var act = async () => await interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) => throw expected);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.Should().BeSameAs(expected);
    }

    [UnitTest]
    public async Task ServerStreamingServerHandler_MapsServiceUnavailableException_ToUnavailable()
    {
        var interceptor = new GrpcExceptionInterceptor(new ListLogger<GrpcExceptionInterceptor>());
        using var context = new TestServerCallContext();
        var responseStream = new TestServerStreamWriter<string>();

        var act = async () => await interceptor.ServerStreamingServerHandler(
            "request",
            responseStream,
            context,
            (_, _, _) => throw new ServiceUnavailableException("downstream unavailable", 10));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.Unavailable);
        exception.Which.Status.Detail.Should().Be("The service is temporarily unavailable. Please try again later.");
        exception.Which.Trailers.Single(entry => entry.Key == BackpressureMetadata.ErrorCodeKey).Value
            .Should().Be(BackpressureMetadata.ServiceUnavailableCode);
        exception.Which.Trailers.Single(entry => entry.Key == BackpressureMetadata.RetryableKey).Value
            .Should().Be("true");
        exception.Which.Trailers.Single(entry => entry.Key == BackpressureMetadata.RetryAfterKey).Value
            .Should().Be("10");
        exception.Which.Trailers.Single(entry => entry.Key == BackpressureMetadata.CorrelationIdKey).Value
            .Should().NotBeNullOrWhiteSpace();
    }

    private sealed class TestServerCallContext(Metadata? requestHeaders = null) : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Metadata _responseTrailers = new();
        private readonly Metadata _requestHeaders = requestHeaders ?? new Metadata();

        public void Cancel() => _cancellationTokenSource.Cancel();

        public void Dispose() => _cancellationTokenSource.Dispose();

        protected override string MethodCore => "/geospatial.v1.FeatureService/QueryFeatures";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationTokenSource.Token;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }

    private sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;

        public Task WriteAsync(T message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NullScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, EventId EventId, string Message, Exception? Exception);
}
