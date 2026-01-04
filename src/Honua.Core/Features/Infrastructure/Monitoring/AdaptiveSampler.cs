// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Adaptive sampler that dynamically adjusts tracing sampling rates based on
/// system load, error rates, and operation importance.
/// Provides intelligent tracing with minimal performance overhead for production environments.
/// </summary>
public sealed partial class AdaptiveSampler : IAdaptiveSampler, IDisposable
{
    private readonly AdaptiveSamplingOptions _options;
    private readonly ISystemMetricsCollector _metricsCollector;
    private readonly ILogger<AdaptiveSampler> _logger;
    private readonly Timer _evaluationTimer;
    private readonly Random _random = new();

    private double _currentSamplingRate;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new adaptive sampler with configuration and metrics collection.
    /// </summary>
    public AdaptiveSampler(
        IOptions<AdaptiveSamplingOptions> options,
        ISystemMetricsCollector metricsCollector,
        ILogger<AdaptiveSampler> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Volatile.Write(ref _currentSamplingRate, _options.BaseSamplingRate);

        // Start evaluation timer if adaptive sampling is enabled
        if (_options.Enabled)
        {
            _evaluationTimer = new Timer(EvaluateAndAdjustSamplingRate, null,
                _options.EvaluationWindow, _options.EvaluationWindow);

            Log.AdaptiveSamplingInitialized(
                _logger,
                _options.BaseSamplingRate,
                _options.MinSamplingRate,
                _options.MaxSamplingRate,
                _options.EvaluationWindow);
        }
        else
        {
            Log.AdaptiveSamplingDisabled(_logger, _options.BaseSamplingRate);
        }
    }

    /// <summary>
    /// Determines if a specific operation should be sampled based on adaptive rules.
    /// </summary>
    public bool ShouldSample(string operationName, ActivityKind activityKind = ActivityKind.Internal)
    {
        if (_disposed)
        {
            return false;
        }

        if (!_options.Enabled)
        {
            // Fallback to base sampling rate if adaptive sampling is disabled
            return _random.NextDouble() < _options.BaseSamplingRate;
        }

        var importance = ClassifyOperation(operationName);
        var effectiveRate = CalculateEffectiveSamplingRate(importance);

        var shouldSample = _random.NextDouble() < effectiveRate;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.SamplingDecision(_logger, operationName, importance, effectiveRate, shouldSample);
        }

        return shouldSample;
    }

    /// <summary>
    /// Gets the current effective sampling rate.
    /// </summary>
    public double GetCurrentSamplingRate()
    {
        return Volatile.Read(ref _currentSamplingRate);
    }

    /// <summary>
    /// Gets adaptive sampling statistics for monitoring.
    /// </summary>
    public AdaptiveSamplingStats GetStats()
    {
        var metrics = _metricsCollector.GetCurrentMetrics();
        var errorRate = _metricsCollector.GetCurrentErrorRate();

        return new AdaptiveSamplingStats
        {
            CurrentSamplingRate = Volatile.Read(ref _currentSamplingRate),
            SystemLoad = CalculateSystemLoad(metrics),
            ErrorRate = errorRate,
            IsAdaptiveEnabled = _options.Enabled,
            LastEvaluation = DateTime.UtcNow
        };
    }

    private void EvaluateAndAdjustSamplingRate(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var metrics = _metricsCollector.GetCurrentMetrics();
            var errorRate = _metricsCollector.GetCurrentErrorRate();

            var newRate = CalculateAdaptiveSamplingRate(metrics, errorRate);

            var currentRate = Volatile.Read(ref _currentSamplingRate);
            if (Math.Abs(newRate - currentRate) > 0.01) // Only log significant changes
            {
                Log.SamplingRateAdjusted(
                    _logger,
                    currentRate,
                    newRate,
                    metrics.CpuUsagePercentage,
                    metrics.MemoryUsagePercentage,
                    metrics.ActiveRequests,
                    metrics.AverageResponseTimeMs,
                    errorRate);
            }

            Volatile.Write(ref _currentSamplingRate, newRate);
        }
        catch (Exception ex)
        {
            Log.SamplingEvaluationFailed(_logger, ex, Volatile.Read(ref _currentSamplingRate));
        }
    }

    private double CalculateAdaptiveSamplingRate(SystemMetrics metrics, double errorRate)
    {
        var baseRate = _options.BaseSamplingRate;

        // Calculate system load factor (0.0 = no load, 1.0 = high load)
        var loadFactor = CalculateSystemLoad(metrics);

        // Calculate error factor (increases sampling during errors)
        var errorFactor = CalculateErrorFactor(errorRate);

        // Apply load reduction (higher load = lower sampling)
        var loadAdjustedRate = baseRate * Math.Max(1.0 - loadFactor, 0.1);

        // Apply error boost (higher errors = higher sampling)
        var errorAdjustedRate = loadAdjustedRate * errorFactor;

        // Clamp to configured bounds
        return Math.Clamp(errorAdjustedRate, _options.MinSamplingRate, _options.MaxSamplingRate);
    }

    private double CalculateSystemLoad(SystemMetrics metrics)
    {
        var cpuLoad = Math.Max(0, (metrics.CpuUsagePercentage - 20) / _options.Load.CpuThreshold);
        var memoryLoad = Math.Max(0, (metrics.MemoryUsagePercentage - 30) / _options.Load.MemoryThreshold);
        var requestLoad = Math.Max(0, (metrics.ActiveRequests - 10.0) / _options.Load.ActiveRequestThreshold);
        var latencyLoad = Math.Max(0, (metrics.AverageResponseTimeMs - 100) / _options.Load.ResponseTimeThresholdMs);

        // Weighted average of load factors
        return Math.Min(1.0, (cpuLoad * 0.3 + memoryLoad * 0.2 + requestLoad * 0.3 + latencyLoad * 0.2));
    }

    private double CalculateErrorFactor(double errorRate)
    {
        if (errorRate < _options.Error.ErrorRateThreshold)
        {
            return 1.0; // No error boost
        }

        // Linear scaling from threshold to multiplier
        var excessErrorRate = errorRate - _options.Error.ErrorRateThreshold;
        var boostFactor = 1.0 + (excessErrorRate / _options.Error.ErrorRateThreshold) *
            (_options.Error.ErrorMultiplier - 1.0);

        return Math.Min(boostFactor, _options.Error.ErrorMultiplier);
    }

    private double CalculateEffectiveSamplingRate(OperationImportance importance)
    {
        if (!_options.Operations.Enabled)
        {
            return Volatile.Read(ref _currentSamplingRate);
        }

        var baseRate = importance switch
        {
            OperationImportance.Critical => _options.Operations.CriticalRate,
            OperationImportance.Important => _options.Operations.ImportantRate,
            OperationImportance.Normal => _options.Operations.NormalRate,
            OperationImportance.Background => _options.Operations.BackgroundRate,
            _ => _options.Operations.NormalRate
        };

        // For critical operations, use the configured rate without adaptive adjustment
        if (importance == OperationImportance.Critical)
        {
            return baseRate;
        }

        // For other operations, blend with current adaptive rate
        return Math.Min(baseRate, _currentSamplingRate * 2.0); // Allow up to 2x current rate for important ops
    }

    private static OperationImportance ClassifyOperation(string operationName)
    {
        // Use ReadOnlySpan for efficient string operations
        var span = operationName.AsSpan();

        return span switch
        {
            // Critical operations - authentication, security, data modification
            var s when s.Contains("auth", StringComparison.OrdinalIgnoreCase) => OperationImportance.Critical,
            var s when s.Contains("security", StringComparison.OrdinalIgnoreCase) => OperationImportance.Critical,
            var s when s.Contains("create", StringComparison.OrdinalIgnoreCase) => OperationImportance.Critical,
            var s when s.Contains("update", StringComparison.OrdinalIgnoreCase) => OperationImportance.Critical,
            var s when s.Contains("delete", StringComparison.OrdinalIgnoreCase) => OperationImportance.Critical,
            var s when s.Contains("edit", StringComparison.OrdinalIgnoreCase) => OperationImportance.Critical,

            // Important operations - complex queries, spatial operations
            var s when s.Contains("spatial", StringComparison.OrdinalIgnoreCase) => OperationImportance.Important,
            var s when s.Contains("bulk", StringComparison.OrdinalIgnoreCase) => OperationImportance.Important,
            var s when s.Contains("import", StringComparison.OrdinalIgnoreCase) => OperationImportance.Important,
            var s when s.Contains("tile", StringComparison.OrdinalIgnoreCase) => OperationImportance.Important,

            // Background operations - monitoring, health checks
            var s when s.Contains("health", StringComparison.OrdinalIgnoreCase) => OperationImportance.Background,
            var s when s.Contains("metrics", StringComparison.OrdinalIgnoreCase) => OperationImportance.Background,
            var s when s.Contains("ping", StringComparison.OrdinalIgnoreCase) => OperationImportance.Background,

            // Default to normal for everything else
            _ => OperationImportance.Normal
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _evaluationTimer?.Dispose();

        Log.AdaptiveSamplerDisposed(_logger);
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Information,
            Message = "Adaptive sampling initialized with base rate {BaseSamplingRate:P2}, range [{MinRate:P2} - {MaxRate:P2}], evaluation window {EvaluationWindow}")]
        public static partial void AdaptiveSamplingInitialized(
            ILogger logger,
            double baseSamplingRate,
            double minRate,
            double maxRate,
            TimeSpan evaluationWindow);

        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Information,
            Message = "Adaptive sampling disabled, using static rate {SamplingRate:P2}")]
        public static partial void AdaptiveSamplingDisabled(ILogger logger, double samplingRate);

        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Debug,
            Message = "Sampling decision for {OperationName}: importance={Importance}, effectiveRate={EffectiveRate:P3}, shouldSample={ShouldSample}")]
        public static partial void SamplingDecision(
            ILogger logger,
            string operationName,
            OperationImportance importance,
            double effectiveRate,
            bool shouldSample);

        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Information,
            Message = "Adaptive sampling rate adjusted from {OldRate:P2} to {NewRate:P2} (Load: CPU={CpuUsage:F1}%, Memory={MemoryUsage:F1}%, Requests={ActiveRequests}, AvgResponse={AvgResponseTime:F1}ms, ErrorRate={ErrorRate:F1}%)")]
        public static partial void SamplingRateAdjusted(
            ILogger logger,
            double oldRate,
            double newRate,
            double cpuUsage,
            double memoryUsage,
            int activeRequests,
            double avgResponseTime,
            double errorRate);

        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Warning,
            Message = "Error during adaptive sampling evaluation, maintaining current rate {CurrentRate:P2}")]
        public static partial void SamplingEvaluationFailed(ILogger logger, Exception exception, double currentRate);

        [LoggerMessage(
            EventId = 6,
            Level = LogLevel.Information,
            Message = "Adaptive sampler disposed")]
        public static partial void AdaptiveSamplerDisposed(ILogger logger);
    }
}

/// <summary>
/// Interface for adaptive sampling functionality.
/// </summary>
public interface IAdaptiveSampler
{
    /// <summary>
    /// Determines whether an operation should be traced.
    /// </summary>
    bool ShouldSample(string operationName, ActivityKind activityKind = ActivityKind.Internal);

    /// <summary>
    /// Gets the current effective sampling rate.
    /// </summary>
    double GetCurrentSamplingRate();

    /// <summary>
    /// Gets statistics about the adaptive sampling behavior.
    /// </summary>
    AdaptiveSamplingStats GetStats();
}

/// <summary>
/// Statistics about adaptive sampling behavior.
/// </summary>
public sealed record AdaptiveSamplingStats
{
    /// <summary>Current effective sampling rate.</summary>
    public double CurrentSamplingRate { get; init; }

    /// <summary>Calculated system load factor (0.0 - 1.0).</summary>
    public double SystemLoad { get; init; }

    /// <summary>Current error rate percentage.</summary>
    public double ErrorRate { get; init; }

    /// <summary>Whether adaptive sampling is enabled.</summary>
    public bool IsAdaptiveEnabled { get; init; }

    /// <summary>Timestamp of the last evaluation.</summary>
    public DateTime LastEvaluation { get; init; }
}
