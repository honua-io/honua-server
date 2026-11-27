// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

public sealed class TimerCallbackResilienceTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void HistoricalPatternTracker_AnalyzePatterns_AbsorbsAndLogsNonFatalException()
    {
        var logger = CreateEnabledLogger<AdaptiveSampler>();
        using var tracker = new HistoricalPatternTracker(logger);

        // Null metrics triggers a NullReferenceException during pattern analysis.
        tracker.RecordDataPoint(DateTime.UtcNow, null!, 0.0, 0.5);

        var exception = Record.Exception(() => tracker.AnalyzePatterns(null));

        Assert.Null(exception);
        Assert.Contains(
            logger.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(ILogger.Log) &&
                    call.GetArguments().OfType<NullReferenceException>().Any());
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void EnhancedExceptionTelemetry_CleanupRateLimiters_AbsorbsAndLogsNonFatalException()
    {
        var logger = CreateEnabledLogger<EnhancedExceptionTelemetry>();
        var options = new ExceptionTelemetryOptions
        {
            // Exceeds DateTime.UtcNow.Ticks so DateTime.UtcNow.Subtract(...) overflows.
            RateLimitWindow = TimeSpan.MaxValue
        };
        using var telemetry = new EnhancedExceptionTelemetry(logger, Options.Create(options));

        var exception = Record.Exception(() => telemetry.CleanupRateLimiters(null));

        Assert.Null(exception);
        Assert.Contains(
            logger.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(ILogger.Log) &&
                    call.GetArguments().OfType<OverflowException>().Any());
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void EnhancedExceptionTelemetry_CleanupRateLimiters_HandlesConcurrentRecordingWithoutThrowing()
    {
        var logger = CreateEnabledLogger<EnhancedExceptionTelemetry>();
        var options = new ExceptionTelemetryOptions
        {
            RateLimitWindow = TimeSpan.FromMilliseconds(1),
            MaxOccurrencesPerWindow = 100
        };
        using var telemetry = new EnhancedExceptionTelemetry(logger, Options.Create(options));

        var exception = Record.Exception(() =>
        {
            Parallel.For(0, 32, index =>
            {
                telemetry.RecordException(new InvalidOperationException($"err-{index}"), $"corr-{index}", "op");
                telemetry.CleanupRateLimiters(null);
            });
        });

        Assert.Null(exception);
    }

    private static ILogger<T> CreateEnabledLogger<T>()
    {
        var logger = Substitute.For<ILogger<T>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return logger;
    }
}
