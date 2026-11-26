// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

public sealed class TimerCallbackResilienceTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void HistoricalPatternTracker_AnalyzePatterns_AbsorbsAndLogsNonFatalException()
    {
        var logger = new RecordingLogger<AdaptiveSampler>();
        using var tracker = new HistoricalPatternTracker(logger);

        // Null metrics triggers a NullReferenceException during pattern analysis.
        tracker.RecordDataPoint(DateTime.UtcNow, null!, 0.0, 0.5);

        var exception = Record.Exception(() => tracker.AnalyzePatterns(null));

        Assert.Null(exception);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is NullReferenceException);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void EnhancedExceptionTelemetry_CleanupRateLimiters_AbsorbsAndLogsNonFatalException()
    {
        var logger = new RecordingLogger<EnhancedExceptionTelemetry>();
        var options = new ExceptionTelemetryOptions
        {
            // Exceeds DateTime.UtcNow.Ticks so DateTime.UtcNow.Subtract(...) overflows.
            RateLimitWindow = TimeSpan.MaxValue
        };
        using var telemetry = new EnhancedExceptionTelemetry(logger, Options.Create(options));

        var exception = Record.Exception(() => telemetry.CleanupRateLimiters(null));

        Assert.Null(exception);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is OverflowException);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void EnhancedExceptionTelemetry_CleanupRateLimiters_HandlesConcurrentRecordingWithoutThrowing()
    {
        var logger = new RecordingLogger<EnhancedExceptionTelemetry>();
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
