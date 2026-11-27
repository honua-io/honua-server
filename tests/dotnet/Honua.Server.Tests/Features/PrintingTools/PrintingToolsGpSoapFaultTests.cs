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
using NSubstitute;

namespace Honua.Server.Tests.Features.PrintingTools;

[Protocol(TestProtocols.PrintingTools)]
public sealed class PrintingToolsGpSoapFaultTests
{
    [UnitTest]
    public void LogGpSoapFailureAndSetRetryHeaders_TimeoutCancellation_LogsWarningAndSets503RetryHeaders()
    {
        var (context, logger) = CreateHttpContextWithLogger();

        var statusCode = PrintingToolsEndpoints.LogGpSoapFailureAndSetRetryHeaders(
            context,
            "Execute",
            new OperationCanceledException("render timed out"));

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("5");
        AssertSingleLog(logger, LogLevel.Warning, eventId: 5534);
    }

    [UnitTest]
    public void LogGpSoapFailureAndSetRetryHeaders_ServiceUnavailable_LogsWarningAndUsesExceptionRetryAfter()
    {
        var (context, logger) = CreateHttpContextWithLogger();

        var statusCode = PrintingToolsEndpoints.LogGpSoapFailureAndSetRetryHeaders(
            context,
            "SubmitJob",
            new ServiceUnavailableException("queue busy", retryAfterSeconds: 15));

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("15");
        AssertSingleLog(logger, LogLevel.Warning, eventId: 5534);
    }

    [UnitTest]
    public void LogGpSoapFailureAndSetRetryHeaders_UnexpectedException_LogsErrorAndReturns500WithoutRetryHeaders()
    {
        var (context, logger) = CreateHttpContextWithLogger();

        var statusCode = PrintingToolsEndpoints.LogGpSoapFailureAndSetRetryHeaders(
            context,
            "GetToolInfos",
            new InvalidOperationException("unexpected failure"));

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        context.Response.Headers.ContainsKey("Honua-Retryable").Should().BeFalse();
        context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
        AssertSingleLog(logger, LogLevel.Error, eventId: 5535);
    }

    private static (DefaultHttpContext Context, ILogger Logger) CreateHttpContextWithLogger()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var provider = Substitute.For<ILoggerProvider>();
        provider.CreateLogger(Arg.Any<string>()).Returns(logger);
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(provider))
            .BuildServiceProvider();
        return (new DefaultHttpContext { RequestServices = services }, logger);
    }

    private static void AssertSingleLog(ILogger logger, LogLevel expectedLevel, int eventId)
    {
        var logCall = logger.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ILogger.Log));
        var args = logCall.GetArguments();
        args[0].Should().Be(expectedLevel);
        ((EventId)args[1]!).Id.Should().Be(eventId);
    }
}
