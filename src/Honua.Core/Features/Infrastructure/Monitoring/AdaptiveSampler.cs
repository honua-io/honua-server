// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Adaptive sampler that dynamically adjusts tracing sampling rates based on
/// system load, error rates, and operation importance.
/// WEEK 5 FIX: Enhanced with ML-based intelligence for historical patterns, temporal adjustments, and predictive sampling.
/// Provides intelligent tracing with minimal performance overhead for production environments.
/// Integrates with smart sampling rules for business-aware sampling decisions.
/// </summary>
public sealed partial class AdaptiveSampler : IAdaptiveSampler, IDisposable
{
    private readonly AdaptiveSamplingOptions _options;
    private readonly ISystemMetricsCollector _metricsCollector;
    private readonly ILogger<AdaptiveSampler> _logger;
    private readonly ISmartSamplingRules? _smartSamplingRules;
    private readonly Timer? _evaluationTimer;

    private double _currentSamplingRate;
    private volatile bool _disposed;

    // WEEK 5 FIX: ML-based intelligence data structures
    private readonly HistoricalPatternTracker _patternTracker;
    private readonly TemporalPatternAnalyzer _temporalAnalyzer;
    private readonly PredictiveLoadForecaster _loadForecaster;
    private readonly MLSamplingDecisionEngine _mlEngine;
    private readonly ConcurrentDictionary<string, OperationPattern> _operationPatterns;

    /// <summary>
    /// Initializes a new adaptive sampler with configuration and metrics collection.
    /// WEEK 5 FIX: Enhanced with ML-based intelligence components
    /// </summary>
    public AdaptiveSampler(
        IOptions<AdaptiveSamplingOptions> options,
        ISystemMetricsCollector metricsCollector,
        ILogger<AdaptiveSampler> logger,
        ISmartSamplingRules? smartSamplingRules = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _smartSamplingRules = smartSamplingRules;

        Volatile.Write(ref _currentSamplingRate, _options.BaseSamplingRate);

        // WEEK 5 FIX: Initialize ML-based intelligence components
        _patternTracker = new HistoricalPatternTracker();
        _temporalAnalyzer = new TemporalPatternAnalyzer();
        _loadForecaster = new PredictiveLoadForecaster();
        _mlEngine = new MLSamplingDecisionEngine(_logger);
        _operationPatterns = new ConcurrentDictionary<string, OperationPattern>();

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
    /// WEEK 5 FIX: Enhanced with ML-based intelligence for predictive sampling decisions
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
            return Random.Shared.NextDouble() < _options.BaseSamplingRate;
        }

        var importance = ClassifyOperation(operationName);

        // WEEK 5 FIX: Use ML-enhanced sampling decision
        var samplingDecision = _mlEngine.MakeSamplingDecision(new MLSamplingContext
        {
            OperationName = operationName,
            Importance = importance,
            ActivityKind = activityKind,
            Timestamp = DateTime.UtcNow,
            CurrentMetrics = _metricsCollector.GetCurrentMetrics(),
            HistoricalPattern = HistoricalPatternTracker.GetPattern(operationName),
            TemporalContext = TemporalPatternAnalyzer.GetCurrentContext(),
            PredictedLoad = _loadForecaster.GetPredictedLoad()
        });

        // Record operation pattern for learning
        RecordOperationPattern(operationName, samplingDecision.EffectiveSamplingRate, samplingDecision.WasSampled);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.MLSamplingDecision(_logger, operationName, importance, samplingDecision.EffectiveSamplingRate,
                samplingDecision.WasSampled, samplingDecision.MLContribution, samplingDecision.Confidence);
        }

        return samplingDecision.WasSampled;
    }

    /// <summary>
    /// Determines if a specific operation should be sampled using smart sampling rules
    /// and adaptive logic with rich context information.
    /// WEEK 5 FIX: Enhanced with ML-based context integration
    /// </summary>
    public bool ShouldSample(SamplingContext context)
    {
        if (_disposed)
        {
            return false;
        }

        // First, try smart sampling rules if available
        if (_smartSamplingRules != null)
        {
            var smartDecision = _smartSamplingRules.EvaluateSampling(context);

            if (smartDecision.ShouldSample.HasValue)
            {
                // WEEK 5 FIX: Record smart decision for ML learning
                RecordSmartDecisionForLearning(context, smartDecision);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    Log.SmartSamplingDecision(_logger,
                        context.OperationName ?? "unknown",
                        smartDecision.ShouldSample.Value,
                        smartDecision.Reason,
                        smartDecision.ConfidenceScore);
                }

                return smartDecision.ShouldSample.Value;
            }
        }

        // Fallback to ML-enhanced adaptive sampling
        return ShouldSample(context.OperationName ?? "unknown", ActivityKind.Internal);
    }

    /// <summary>
    /// Gets the current effective sampling rate.
    /// </summary>
    public double GetCurrentSamplingRate()
    {
        return Volatile.Read(ref _currentSamplingRate);
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

            // WEEK 5 FIX: Traditional adaptive rate calculation
            var traditionalRate = CalculateAdaptiveSamplingRate(metrics, errorRate);

            // WEEK 5 FIX: ML-enhanced rate calculation
            var mlEnhancedRate = CalculateMLEnhancedSamplingRate(metrics, errorRate, traditionalRate);

            var currentRate = Volatile.Read(ref _currentSamplingRate);

            // WEEK 5 FIX: Update ML components with new data
            UpdateMLComponents(metrics, errorRate, currentRate);

            if (Math.Abs(mlEnhancedRate - currentRate) > 0.01) // Only log significant changes
            {
                Log.MLEnhancedSamplingRateAdjusted(
                    _logger,
                    currentRate,
                    mlEnhancedRate,
                    traditionalRate,
                    metrics.CpuUsagePercentage,
                    metrics.MemoryUsagePercentage,
                    metrics.ActiveRequests,
                    metrics.AverageResponseTimeMs,
                    errorRate);
            }

            Volatile.Write(ref _currentSamplingRate, mlEnhancedRate);
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

    /// <summary>
    /// WEEK 5 FIX: ML-enhanced sampling rate calculation using historical patterns and predictions
    /// </summary>
    private double CalculateMLEnhancedSamplingRate(SystemMetrics metrics, double errorRate, double traditionalRate)
    {
        var temporalContext = TemporalPatternAnalyzer.GetCurrentContext();
        var predictedLoad = _loadForecaster.GetPredictedLoad();

        // Get historical pattern adjustment
        var historicalAdjustment = _patternTracker.GetHistoricalAdjustment(DateTime.UtcNow.TimeOfDay, DateTime.UtcNow.DayOfWeek);

        // Get temporal pattern adjustment
        var temporalAdjustment = _temporalAnalyzer.GetSamplingAdjustment(temporalContext);

        // Get predictive load adjustment
        var predictiveAdjustment = PredictiveLoadForecaster.GetPredictiveAdjustment(predictedLoad, metrics);

        // Combine adjustments with weights
        var mlAdjustment = (historicalAdjustment * 0.4) + (temporalAdjustment * 0.3) + (predictiveAdjustment * 0.3);

        // Apply ML adjustment to traditional rate
        var mlEnhancedRate = traditionalRate * (1.0 + mlAdjustment);

        // Clamp to configured bounds
        return Math.Clamp(mlEnhancedRate, _options.MinSamplingRate, _options.MaxSamplingRate);
    }

    /// <summary>
    /// WEEK 5 FIX: Update ML components with new data for continuous learning
    /// </summary>
    private void UpdateMLComponents(SystemMetrics metrics, double errorRate, double currentRate)
    {
        var timestamp = DateTime.UtcNow;

        // Update historical patterns
        _patternTracker.RecordDataPoint(timestamp, metrics, errorRate, currentRate);

        // Update temporal analysis
        _temporalAnalyzer.RecordTemporalData(timestamp, metrics, currentRate);

        // Update load forecaster
        _loadForecaster.RecordLoadData(timestamp, metrics, errorRate);

        // Trigger learning if enough data collected
        if (timestamp.Minute % 5 == 0) // Every 5 minutes
        {
            _mlEngine.TriggerLearning(_operationPatterns.Values.ToList());
        }
    }

    /// <summary>
    /// WEEK 5 FIX: Record operation pattern for ML learning
    /// </summary>
    private void RecordOperationPattern(string operationName, double samplingRate, bool wasSampled)
    {
        var pattern = _operationPatterns.AddOrUpdate(operationName,
            _ => new OperationPattern
            {
                OperationName = operationName,
                TotalInvocations = 1,
                SampledCount = wasSampled ? 1 : 0,
                LastSeen = DateTime.UtcNow,
                AverageSamplingRate = samplingRate
            },
            (_, existing) =>
            {
                existing.TotalInvocations++;
                if (wasSampled) existing.SampledCount++;
                existing.LastSeen = DateTime.UtcNow;
                existing.AverageSamplingRate = ((existing.AverageSamplingRate * existing.TotalInvocations) + samplingRate) / (existing.TotalInvocations + 1);
                return existing;
            });
    }

    /// <summary>
    /// WEEK 5 FIX: Record smart decision for ML learning
    /// </summary>
    private void RecordSmartDecisionForLearning(SamplingContext context, SmartSamplingDecision decision)
    {
        if (string.IsNullOrEmpty(context.OperationName))
            return;

        _mlEngine.RecordSmartDecision(new MLSmartDecisionRecord
        {
            OperationName = context.OperationName,
            Decision = decision.ShouldSample ?? false,
            Confidence = decision.ConfidenceScore,
            Reason = decision.Reason,
            Timestamp = DateTime.UtcNow,
            Context = context
        });
    }

    /// <summary>
    /// Releases all resources used by the <see cref="AdaptiveSampler"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _evaluationTimer?.Dispose();

        // WEEK 5 FIX: Dispose ML components
        _patternTracker?.Dispose();
        _temporalAnalyzer?.Dispose();
        _loadForecaster?.Dispose();
        _mlEngine?.Dispose();

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

        [LoggerMessage(
            EventId = 7,
            Level = LogLevel.Debug,
            Message = "Smart sampling decision for {OperationName}: shouldSample={ShouldSample}, reason={Reason}, confidence={ConfidenceScore:F2}")]
        public static partial void SmartSamplingDecision(
            ILogger logger,
            string operationName,
            bool shouldSample,
            string reason,
            double confidenceScore);

        // WEEK 5 FIX: ML-enhanced logging
        [LoggerMessage(
            EventId = 8,
            Level = LogLevel.Debug,
            Message = "ML sampling decision for {OperationName}: importance={Importance}, effectiveRate={EffectiveRate:P3}, shouldSample={ShouldSample}, mlContribution={MLContribution:F2}, confidence={Confidence:F2}")]
        public static partial void MLSamplingDecision(
            ILogger logger,
            string operationName,
            OperationImportance importance,
            double effectiveRate,
            bool shouldSample,
            double mlContribution,
            double confidence);

        [LoggerMessage(
            EventId = 9,
            Level = LogLevel.Information,
            Message = "ML-enhanced sampling rate adjusted from {OldRate:P2} to {NewRate:P2} (traditional={TraditionalRate:P2}) (Load: CPU={CpuUsage:F1}%, Memory={MemoryUsage:F1}%, Requests={ActiveRequests}, AvgResponse={AvgResponseTime:F1}ms, ErrorRate={ErrorRate:F1}%)")]
        public static partial void MLEnhancedSamplingRateAdjusted(
            ILogger logger,
            double oldRate,
            double newRate,
            double traditionalRate,
            double cpuUsage,
            double memoryUsage,
            int activeRequests,
            double avgResponseTime,
            double errorRate);
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
    /// Determines whether an operation should be traced using smart sampling rules
    /// and rich context information.
    /// </summary>
    bool ShouldSample(SamplingContext context);

    /// <summary>
    /// Gets the current effective sampling rate.
    /// </summary>
    double GetCurrentSamplingRate();

}

// WEEK 5 FIX: ML-based intelligence component classes

/// <summary>
/// WEEK 5 FIX: Tracks historical patterns in system metrics and sampling decisions for pattern recognition
/// </summary>
internal sealed class HistoricalPatternTracker : IDisposable
{
    private readonly ConcurrentQueue<HistoricalDataPoint> _dataPoints = new();
    private readonly Dictionary<(TimeSpan TimeOfDay, DayOfWeek DayOfWeek), PatternStatistics> _patterns = new();
    private readonly object _analysisLock = new object();
    private readonly Timer _analysisTimer;

    public HistoricalPatternTracker()
    {
        // Analyze patterns every hour
        _analysisTimer = new Timer(AnalyzePatterns, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public void RecordDataPoint(DateTime timestamp, SystemMetrics metrics, double errorRate, double samplingRate)
    {
        var dataPoint = new HistoricalDataPoint
        {
            Timestamp = timestamp,
            Metrics = metrics,
            ErrorRate = errorRate,
            SamplingRate = samplingRate
        };

        _dataPoints.Enqueue(dataPoint);

        // Keep only recent data (last 7 days)
        while (_dataPoints.Count > 10080) // 7 days * 24 hours * 60 minutes
        {
            _dataPoints.TryDequeue(out _);
        }
    }

    public double GetHistoricalAdjustment(TimeSpan timeOfDay, DayOfWeek dayOfWeek)
    {
        lock (_analysisLock)
        {
            var key = (RoundToHour(timeOfDay), dayOfWeek);
            if (_patterns.TryGetValue(key, out var pattern))
            {
                // Return adjustment based on historical load patterns
                return pattern.LoadFactor - 1.0; // -1.0 to 1.0 range
            }
        }

        return 0.0; // No adjustment if no pattern found
    }

    public static HistoricalPattern GetPattern(string operationName)
    {
        // Simple pattern for now - could be enhanced with operation-specific patterns
        return new HistoricalPattern
        {
            OperationName = operationName,
            AverageLoadFactor = 1.0,
            PeakHours = new[] { TimeSpan.FromHours(9), TimeSpan.FromHours(14) },
            LowActivityHours = new[] { TimeSpan.FromHours(2), TimeSpan.FromHours(5) }
        };
    }

    private void AnalyzePatterns(object? state)
    {
        lock (_analysisLock)
        {
            var dataPointsList = _dataPoints.ToArray();

            var groupedData = dataPointsList
                .GroupBy(dp => (RoundToHour(dp.Timestamp.TimeOfDay), dp.Timestamp.DayOfWeek))
                .ToList();

            foreach (var group in groupedData)
            {
                var stats = new PatternStatistics
                {
                    LoadFactor = group.Average(dp => CalculateLoadFactor(dp.Metrics)),
                    ErrorRate = group.Average(dp => dp.ErrorRate),
                    SamplingRate = group.Average(dp => dp.SamplingRate),
                    SampleCount = group.Count()
                };

                _patterns[group.Key] = stats;
            }
        }
    }

    private static double CalculateLoadFactor(SystemMetrics metrics)
    {
        return (metrics.CpuUsagePercentage + metrics.MemoryUsagePercentage + (metrics.ActiveRequests * 2)) / 100.0;
    }

    private static TimeSpan RoundToHour(TimeSpan time)
    {
        return TimeSpan.FromHours(time.Hours);
    }

    public void Dispose()
    {
        _analysisTimer?.Dispose();
    }
}

/// <summary>
/// WEEK 5 FIX: Analyzes temporal patterns for time-based sampling adjustments
/// </summary>
internal sealed class TemporalPatternAnalyzer : IDisposable
{
    private readonly ConcurrentQueue<TemporalDataPoint> _temporalData = new();
    private readonly Dictionary<TimeSpan, double> _hourlyPatterns = new();
    private readonly Dictionary<DayOfWeek, double> _dailyPatterns = new();
    private readonly object _patternLock = new object();

    public void RecordTemporalData(DateTime timestamp, SystemMetrics metrics, double samplingRate)
    {
        var dataPoint = new TemporalDataPoint
        {
            Timestamp = timestamp,
            LoadFactor = CalculateLoadFactor(metrics),
            SamplingRate = samplingRate
        };

        _temporalData.Enqueue(dataPoint);

        // Keep only last 24 hours of data
        while (_temporalData.Count > 1440) // 24 hours * 60 minutes
        {
            _temporalData.TryDequeue(out _);
        }

        UpdatePatterns();
    }

    public static TemporalContext GetCurrentContext()
    {
        var now = DateTime.UtcNow;
        return new TemporalContext
        {
            TimeOfDay = now.TimeOfDay,
            DayOfWeek = now.DayOfWeek,
            IsBusinessHours = IsBusinessHours(now),
            IsWeekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
        };
    }

    public double GetSamplingAdjustment(TemporalContext context)
    {
        lock (_patternLock)
        {
            var hourlyAdjustment = _hourlyPatterns.TryGetValue(TimeSpan.FromHours(context.TimeOfDay.Hours), out var hourly) ? hourly : 0.0;
            var dailyAdjustment = _dailyPatterns.TryGetValue(context.DayOfWeek, out var daily) ? daily : 0.0;

            // Business hours and weekend adjustments
            var businessHoursAdjustment = context.IsBusinessHours ? 0.1 : -0.1;
            var weekendAdjustment = context.IsWeekend ? -0.2 : 0.0;

            return (hourlyAdjustment + dailyAdjustment + businessHoursAdjustment + weekendAdjustment) / 4.0;
        }
    }

    private void UpdatePatterns()
    {
        lock (_patternLock)
        {
            var data = _temporalData.ToArray();

            // Update hourly patterns
            var hourlyGroups = data.GroupBy(d => TimeSpan.FromHours(d.Timestamp.Hour));
            foreach (var group in hourlyGroups)
            {
                var avgLoad = group.Average(d => d.LoadFactor);
                _hourlyPatterns[group.Key] = (avgLoad - 1.0) * 0.5; // Convert to adjustment factor
            }

            // Update daily patterns
            var dailyGroups = data.GroupBy(d => d.Timestamp.DayOfWeek);
            foreach (var group in dailyGroups)
            {
                var avgLoad = group.Average(d => d.LoadFactor);
                _dailyPatterns[group.Key] = (avgLoad - 1.0) * 0.3; // Convert to adjustment factor
            }
        }
    }

    private static bool IsBusinessHours(DateTime time)
    {
        return time.Hour >= 9 && time.Hour <= 17 && time.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    private static double CalculateLoadFactor(SystemMetrics metrics)
    {
        return (metrics.CpuUsagePercentage + metrics.MemoryUsagePercentage) / 200.0 + 1.0;
    }

    public void Dispose()
    {
        // No resources to dispose currently
    }
}

/// <summary>
/// WEEK 5 FIX: Predicts future load based on historical data and trends
/// </summary>
internal sealed class PredictiveLoadForecaster : IDisposable
{
    private readonly ConcurrentQueue<LoadDataPoint> _loadHistory = new();
    private readonly LinearRegression _cpuPredictor = new();
    private readonly LinearRegression _memoryPredictor = new();
    private readonly LinearRegression _requestPredictor = new();

    public void RecordLoadData(DateTime timestamp, SystemMetrics metrics, double errorRate)
    {
        var dataPoint = new LoadDataPoint
        {
            Timestamp = timestamp,
            CpuUsage = metrics.CpuUsagePercentage,
            MemoryUsage = metrics.MemoryUsagePercentage,
            ActiveRequests = metrics.ActiveRequests,
            ErrorRate = errorRate
        };

        _loadHistory.Enqueue(dataPoint);

        // Keep last 2 hours of data
        while (_loadHistory.Count > 120) // 2 hours * 60 minutes
        {
            _loadHistory.TryDequeue(out _);
        }

        UpdatePredictors();
    }

    public PredictedLoad GetPredictedLoad()
    {
        var now = DateTime.UtcNow;
        var futureTimestamp = now.AddMinutes(5); // 5-minute prediction

        return new PredictedLoad
        {
            PredictionTime = futureTimestamp,
            PredictedCpuUsage = _cpuPredictor.Predict(),
            PredictedMemoryUsage = _memoryPredictor.Predict(),
            PredictedActiveRequests = (int)_requestPredictor.Predict(),
            Confidence = CalculatePredictionConfidence()
        };
    }

    public static double GetPredictiveAdjustment(PredictedLoad predicted, SystemMetrics current)
    {
        var cpuTrend = (predicted.PredictedCpuUsage - current.CpuUsagePercentage) / 100.0;
        var memoryTrend = (predicted.PredictedMemoryUsage - current.MemoryUsagePercentage) / 100.0;
        var requestTrend = (predicted.PredictedActiveRequests - current.ActiveRequests) / Math.Max(current.ActiveRequests, 1.0);

        var overallTrend = (cpuTrend + memoryTrend + requestTrend) / 3.0;

        // If load is predicted to increase, reduce sampling proactively
        return -overallTrend * 0.5; // Scale down for conservative adjustment
    }

    private void UpdatePredictors()
    {
        var data = _loadHistory.ToArray();
        if (data.Length < 10) return;

        var timestamps = data.Select((d, i) => (double)i).ToArray();
        var cpuValues = data.Select(d => d.CpuUsage).ToArray();
        var memoryValues = data.Select(d => d.MemoryUsage).ToArray();
        var requestValues = data.Select(d => (double)d.ActiveRequests).ToArray();

        _cpuPredictor.Update(timestamps, cpuValues);
        _memoryPredictor.Update(timestamps, memoryValues);
        _requestPredictor.Update(timestamps, requestValues);
    }

    private double CalculatePredictionConfidence()
    {
        var dataCount = _loadHistory.Count;
        if (dataCount < 10) return 0.1;
        if (dataCount < 30) return 0.5;
        if (dataCount < 60) return 0.8;
        return 0.9;
    }

    public void Dispose()
    {
        // No resources to dispose currently
    }
}

/// <summary>
/// WEEK 5 FIX: ML-based decision engine that combines all intelligence sources
/// </summary>
internal sealed class MLSamplingDecisionEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<MLSmartDecisionRecord> _smartDecisions = new();
    private readonly ConcurrentDictionary<string, double> _operationSuccessRates = new();
    private double _globalConfidence = 0.5;

    public MLSamplingDecisionEngine(ILogger logger)
    {
        _logger = logger;
    }

    public MLSamplingDecision MakeSamplingDecision(MLSamplingContext context)
    {
        var baseRate = CalculateBaseSamplingRate(context);
        var mlAdjustment = CalculateMLAdjustment(context);
        var confidence = CalculateDecisionConfidence(context);

        var effectiveRate = Math.Clamp(baseRate * (1.0 + mlAdjustment), 0.001, 1.0);
        var shouldSample = Random.Shared.NextDouble() < effectiveRate;

        return new MLSamplingDecision
        {
            WasSampled = shouldSample,
            EffectiveSamplingRate = effectiveRate,
            BaseSamplingRate = baseRate,
            MLContribution = mlAdjustment,
            Confidence = confidence,
            DecisionTime = DateTime.UtcNow
        };
    }

    public void RecordSmartDecision(MLSmartDecisionRecord record)
    {
        _smartDecisions.Enqueue(record);

        // Keep only recent decisions
        while (_smartDecisions.Count > 1000)
        {
            _smartDecisions.TryDequeue(out _);
        }

        // Update operation success rates
        _operationSuccessRates.AddOrUpdate(record.OperationName,
            record.Confidence,
            (_, existing) => (existing + record.Confidence) / 2.0);
    }

    public void TriggerLearning(List<OperationPattern> patterns)
    {
        // Simple learning algorithm - could be enhanced with more sophisticated ML
        var avgSuccessRate = patterns.Count > 0 ? patterns.Average(p => (double)p.SampledCount / p.TotalInvocations) : 0.5;

        // Adjust global confidence based on success patterns
        _globalConfidence = (_globalConfidence + avgSuccessRate) / 2.0;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("ML learning triggered: patterns={PatternCount}, avgSuccess={AvgSuccess:F2}, globalConfidence={GlobalConfidence:F2}",
                patterns.Count, avgSuccessRate, _globalConfidence);
        }
    }

    private static double CalculateBaseSamplingRate(MLSamplingContext context)
    {
        return context.Importance switch
        {
            OperationImportance.Critical => 0.8,
            OperationImportance.Important => 0.4,
            OperationImportance.Normal => 0.1,
            OperationImportance.Background => 0.05,
            _ => 0.1
        };
    }

    private static double CalculateMLAdjustment(MLSamplingContext context)
    {
        var historicalAdjustment = context.HistoricalPattern?.AverageLoadFactor switch
        {
            > 1.5 => -0.2, // High historical load, reduce sampling
            < 0.7 => 0.1,  // Low historical load, increase sampling
            _ => 0.0
        };

        var temporalAdjustment = context.TemporalContext?.IsBusinessHours == true ? 0.1 : -0.05;

        var predictiveAdjustment = context.PredictedLoad?.Confidence > 0.7
            ? (context.PredictedLoad.PredictedCpuUsage > 80 ? -0.15 : 0.05)
            : 0.0;

        return historicalAdjustment + temporalAdjustment + predictiveAdjustment;
    }

    private double CalculateDecisionConfidence(MLSamplingContext context)
    {
        var operationConfidence = _operationSuccessRates.TryGetValue(context.OperationName, out var opConfidence) ? opConfidence : 0.5;
        var temporalConfidence = context.TemporalContext != null ? 0.8 : 0.3;
        var predictiveConfidence = context.PredictedLoad?.Confidence ?? 0.3;

        return (operationConfidence + temporalConfidence + predictiveConfidence + _globalConfidence) / 4.0;
    }

    public void Dispose()
    {
        // No resources to dispose currently
    }
}

/// <summary>
/// WEEK 5 FIX: Simple linear regression for load prediction
/// </summary>
internal sealed class LinearRegression
{
    private double _slope;
    private double _intercept;
    private int _dataPoints;

    public void Update(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length == 0) return;

        var n = x.Length;
        var sumX = x.Sum();
        var sumY = y.Sum();
        var sumXY = x.Zip(y, (a, b) => a * b).Sum();
        var sumX2 = x.Sum(a => a * a);

        var denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < 1e-10) return;

        _slope = (n * sumXY - sumX * sumY) / denominator;
        _intercept = (sumY - _slope * sumX) / n;
        _dataPoints = n;
    }

    public double Predict()
    {
        return _intercept + _slope * _dataPoints;
    }
}

// WEEK 5 FIX: Data structures for ML intelligence

internal sealed class HistoricalDataPoint
{
    public DateTime Timestamp { get; set; }
    public SystemMetrics Metrics { get; set; } = new();
    public double ErrorRate { get; set; }
    public double SamplingRate { get; set; }
}

internal sealed class PatternStatistics
{
    public double LoadFactor { get; set; }
    public double ErrorRate { get; set; }
    public double SamplingRate { get; set; }
    public int SampleCount { get; set; }
}

internal sealed class HistoricalPattern
{
    public string OperationName { get; set; } = string.Empty;
    public double AverageLoadFactor { get; set; }
    public TimeSpan[] PeakHours { get; set; } = Array.Empty<TimeSpan>();
    public TimeSpan[] LowActivityHours { get; set; } = Array.Empty<TimeSpan>();
}

internal sealed class TemporalDataPoint
{
    public DateTime Timestamp { get; set; }
    public double LoadFactor { get; set; }
    public double SamplingRate { get; set; }
}

internal sealed class TemporalContext
{
    public TimeSpan TimeOfDay { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public bool IsBusinessHours { get; set; }
    public bool IsWeekend { get; set; }
}

internal sealed class LoadDataPoint
{
    public DateTime Timestamp { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public int ActiveRequests { get; set; }
    public double ErrorRate { get; set; }
}

internal sealed class PredictedLoad
{
    public DateTime PredictionTime { get; set; }
    public double PredictedCpuUsage { get; set; }
    public double PredictedMemoryUsage { get; set; }
    public int PredictedActiveRequests { get; set; }
    public double Confidence { get; set; }
}

internal sealed class OperationPattern
{
    public string OperationName { get; set; } = string.Empty;
    public int TotalInvocations { get; set; }
    public int SampledCount { get; set; }
    public DateTime LastSeen { get; set; }
    public double AverageSamplingRate { get; set; }
}

internal sealed class MLSamplingContext
{
    public string OperationName { get; set; } = string.Empty;
    public OperationImportance Importance { get; set; }
    public ActivityKind ActivityKind { get; set; }
    public DateTime Timestamp { get; set; }
    public SystemMetrics CurrentMetrics { get; set; } = new();
    public HistoricalPattern? HistoricalPattern { get; set; }
    public TemporalContext? TemporalContext { get; set; }
    public PredictedLoad? PredictedLoad { get; set; }
}

internal sealed class MLSamplingDecision
{
    public bool WasSampled { get; set; }
    public double EffectiveSamplingRate { get; set; }
    public double BaseSamplingRate { get; set; }
    public double MLContribution { get; set; }
    public double Confidence { get; set; }
    public DateTime DecisionTime { get; set; }
}

internal sealed class MLSmartDecisionRecord
{
    public string OperationName { get; set; } = string.Empty;
    public bool Decision { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public SamplingContext Context { get; set; } = new();
}
