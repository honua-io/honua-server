// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Server.Features.PrintingTools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.PrintingTools;

[Protocol(TestProtocols.PrintingTools)]
public sealed class PrintingToolsGpSoapFaultTests
{
    [UnitTest]
    public void LogGpSoapFailureAndSetRetryHeaders_TimeoutCancellation_LogsWarningAndSets503RetryHeaders()
    {
        using var recorder = new RecordingLoggerProvider();
        var context = CreateHttpContext(recorder);

        var statusCode = PrintingToolsEndpoints.LogGpSoapFailureAndSetRetryHeaders(
            context,
            "Execute",
            new OperationCanceledException("render timed out"));

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("5");
        recorder.Entries.Should().ContainSingle();
        recorder.Entries[0].LogLevel.Should().Be(LogLevel.Warning);
        recorder.Entries[0].EventId.Id.Should().Be(5534);
    }

    [UnitTest]
    public void LogGpSoapFailureAndSetRetryHeaders_ServiceUnavailable_LogsWarningAndUsesExceptionRetryAfter()
    {
        using var recorder = new RecordingLoggerProvider();
        var context = CreateHttpContext(recorder);

        var statusCode = PrintingToolsEndpoints.LogGpSoapFailureAndSetRetryHeaders(
            context,
            "SubmitJob",
            new ServiceUnavailableException("queue busy", retryAfterSeconds: 15));

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("15");
        recorder.Entries.Should().ContainSingle();
        recorder.Entries[0].LogLevel.Should().Be(LogLevel.Warning);
        recorder.Entries[0].EventId.Id.Should().Be(5534);
    }

    [UnitTest]
    public void LogGpSoapFailureAndSetRetryHeaders_UnexpectedException_LogsErrorAndReturns500WithoutRetryHeaders()
    {
        using var recorder = new RecordingLoggerProvider();
        var context = CreateHttpContext(recorder);

        var statusCode = PrintingToolsEndpoints.LogGpSoapFailureAndSetRetryHeaders(
            context,
            "GetToolInfos",
            new InvalidOperationException("unexpected failure"));

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        context.Response.Headers.ContainsKey("Honua-Retryable").Should().BeFalse();
        context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
        recorder.Entries.Should().ContainSingle();
        recorder.Entries[0].LogLevel.Should().Be(LogLevel.Error);
        recorder.Entries[0].EventId.Id.Should().Be(5535);
    }

    private static DefaultHttpContext CreateHttpContext(RecordingLoggerProvider provider)
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(provider))
            .BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services };
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<RecordedEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<RecordedEntry> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Add(new RecordedEntry(logLevel, eventId, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record RecordedEntry(LogLevel LogLevel, EventId EventId, Exception? Exception);
}
