// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for anomaly detection.
/// </summary>
public sealed class AnomalyDetectionOptions
{
    /// <summary>
    /// The time window in minutes for collecting baseline metrics.
    /// </summary>
    public int BaselineWindowMinutes { get; set; } = 60;

    /// <summary>
    /// The sensitivity threshold for anomaly detection (0.1 = very sensitive, 0.9 = less sensitive).
    /// </summary>
    public double SensitivityThreshold { get; set; } = 0.3;

    /// <summary>
    /// The minimum number of data points required before anomaly detection is enabled.
    /// </summary>
    public int MinimumDataPoints { get; set; } = 30;

    /// <summary>
    /// The maximum number of historical data points to keep in memory.
    /// </summary>
    public int MaxHistoricalDataPoints { get; set; } = 1000;

    /// <summary>
    /// Whether to enable machine learning-based anomaly detection.
    /// </summary>
    public bool EnableMachineLearning { get; set; } = true;

    /// <summary>
    /// The cooldown period in minutes before re-triggering the same anomaly type.
    /// </summary>
    public int AlertCooldownMinutes { get; set; } = 15;
}

/// <summary>
/// Service for detecting performance and behavior anomalies using statistical and ML techniques.
/// Provides real-time anomaly detection with confidence scoring and adaptive thresholds.
/// </summary>
public interface IAnomalyDetectionService
{
    /// <summary>
    /// Records a metric value for anomaly detection analysis.
    /// </summary>
    /// <param name="metricName">The name of the metric to record.</param>
    /// <param name="value">The metric value.</param>
    /// <param name="tags">Optional tags for metric categorization.</param>
    Task RecordMetricAsync(string metricName, double value, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Checks if the given metric value represents an anomaly.
    /// </summary>
    /// <param name="metricName">The name of the metric to analyze.</param>
    /// <param name="value">The metric value to check.</param>
    /// <param name="tags">Optional tags for metric categorization.</param>
    /// <returns>Anomaly detection result with confidence score.</returns>
    Task<AnomalyDetectionResult> DetectAnomalyAsync(string metricName, double value, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Gets the current baseline statistics for a metric.
    /// </summary>
    /// <param name="metricName">The name of the metric.</param>
    /// <param name="tags">Optional tags for metric categorization.</param>
    /// <returns>Baseline statistics or null if insufficient data.</returns>
    Task<MetricBaseline?> GetBaselineAsync(string metricName, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Gets all detected anomalies within the specified time range.
    /// </summary>
    /// <param name="startTime">The start time for the query.</param>
    /// <param name="endTime">The end time for the query.</param>
    /// <returns>Collection of detected anomalies.</returns>
    Task<IEnumerable<DetectedAnomaly>> GetAnomaliesAsync(DateTimeOffset startTime, DateTimeOffset endTime);
}

/// <summary>
/// Implementation of anomaly detection service using statistical analysis and basic ML techniques.
/// </summary>
internal sealed class AnomalyDetectionService : IAnomalyDetectionService, IHostedService
{
    private readonly AnomalyDetectionOptions _options;
    private readonly ILogger<AnomalyDetectionService> _logger;
    private readonly ConcurrentDictionary<string, MetricHistory> _metricHistories = new();
    private readonly ConcurrentQueue<DetectedAnomaly> _detectedAnomalies = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _alertCooldowns = new();

    private static readonly Action<ILogger, string, double, double, string, Exception?> LogAnomalyDetected =
        LoggerMessage.Define<string, double, double, string>(
            LogLevel.Warning,
            new EventId(1, "AnomalyDetected"),
            "Anomaly detected in metric {MetricName}: {Value} (confidence: {Confidence:F2}, reason: {Reason})");

    public AnomalyDetectionService(
        IOptions<AnomalyDetectionOptions> options,
        ILogger<AnomalyDetectionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task RecordMetricAsync(string metricName, double value, Dictionary<string, string>? tags = null)
    {
        var metricKey = CreateMetricKey(metricName, tags);
        var history = _metricHistories.GetOrAdd(metricKey, _ => new MetricHistory(metricName, tags));

        var dataPoint = new MetricDataPoint
        {
            Timestamp = DateTimeOffset.UtcNow,
            Value = value
        };

        await history.AddDataPointAsync(dataPoint, _options.MaxHistoricalDataPoints);

        // Trigger anomaly detection if we have sufficient data
        if (history.DataPoints.Count() >= _options.MinimumDataPoints)
        {
            var anomalyResult = await DetectAnomalyAsync(metricName, value, tags);
            if (anomalyResult.IsAnomaly)
            {
                await HandleDetectedAnomalyAsync(metricName, value, anomalyResult, tags);
            }
        }
    }

    public async Task<AnomalyDetectionResult> DetectAnomalyAsync(string metricName, double value, Dictionary<string, string>? tags = null)
    {
        var metricKey = CreateMetricKey(metricName, tags);
        if (!_metricHistories.TryGetValue(metricKey, out var history))
        {
            return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0, Reason = "Insufficient historical data" };
        }

        if (history.DataPoints.Count() < _options.MinimumDataPoints)
        {
            return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0, Reason = "Insufficient data points for analysis" };
        }

        var baseline = await CalculateBaselineAsync(history);
        if (baseline == null)
        {
            return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0, Reason = "Unable to calculate baseline" };
        }

        // Statistical anomaly detection using Z-score and IQR methods
        var zScoreResult = DetectZScoreAnomaly(value, baseline);
        var iqrResult = DetectIQRAnomaly(value, baseline);

        // Machine learning-based detection (simplified implementation)
        var mlResult = _options.EnableMachineLearning ? DetectMLAnomaly(value, history) : new AnomalyDetectionResult();

        // Combine results using ensemble approach
        var combinedConfidence = (zScoreResult.Confidence + iqrResult.Confidence + mlResult.Confidence) / 3.0;
        var isAnomaly = combinedConfidence > _options.SensitivityThreshold;

        var result = new AnomalyDetectionResult
        {
            IsAnomaly = isAnomaly,
            Confidence = combinedConfidence,
            Reason = isAnomaly ? GenerateAnomalyReason(value, baseline, zScoreResult, iqrResult, mlResult) : "Value within normal range",
            Baseline = baseline,
            DetectionMethods = new Dictionary<string, double>
            {
                { "z_score", zScoreResult.Confidence },
                { "iqr", iqrResult.Confidence },
                { "machine_learning", mlResult.Confidence }
            }
        };

        return result;
    }

    public async Task<MetricBaseline?> GetBaselineAsync(string metricName, Dictionary<string, string>? tags = null)
    {
        var metricKey = CreateMetricKey(metricName, tags);
        if (!_metricHistories.TryGetValue(metricKey, out var history))
        {
            return null;
        }

        return await CalculateBaselineAsync(history);
    }

    public async Task<IEnumerable<DetectedAnomaly>> GetAnomaliesAsync(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var anomalies = new List<DetectedAnomaly>();

        while (_detectedAnomalies.TryDequeue(out var anomaly))
        {
            if (anomaly.Timestamp >= startTime && anomaly.Timestamp <= endTime)
            {
                anomalies.Add(anomaly);
            }
        }

        return anomalies;
    }

    private async Task<MetricBaseline?> CalculateBaselineAsync(MetricHistory history)
    {
        var cutoffTime = DateTimeOffset.UtcNow.AddMinutes(-_options.BaselineWindowMinutes);
        var recentData = history.DataPoints
            .Where(dp => dp.Timestamp >= cutoffTime)
            .Select(dp => dp.Value)
            .ToArray();

        if (recentData.Length < _options.MinimumDataPoints / 2)
        {
            return null;
        }

        Array.Sort(recentData);

        var mean = recentData.Average();
        var variance = recentData.Sum(x => Math.Pow(x - mean, 2)) / recentData.Length;
        var standardDeviation = Math.Sqrt(variance);

        var q1Index = recentData.Length / 4;
        var q3Index = (3 * recentData.Length) / 4;
        var q1 = recentData[q1Index];
        var q3 = recentData[q3Index];
        var iqr = q3 - q1;

        return new MetricBaseline
        {
            Mean = mean,
            StandardDeviation = standardDeviation,
            Q1 = q1,
            Q3 = q3,
            IQR = iqr,
            Min = recentData[0],
            Max = recentData[^1],
            SampleSize = recentData.Length,
            CalculatedAt = DateTimeOffset.UtcNow
        };
    }

    private AnomalyDetectionResult DetectZScoreAnomaly(double value, MetricBaseline baseline)
    {
        if (baseline.StandardDeviation == 0)
        {
            return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0 };
        }

        var zScore = Math.Abs((value - baseline.Mean) / baseline.StandardDeviation);
        var confidence = zScore > 3.0 ? 1.0 : (zScore > 2.0 ? 0.8 : (zScore > 1.5 ? 0.5 : 0.0));

        return new AnomalyDetectionResult
        {
            IsAnomaly = confidence > _options.SensitivityThreshold,
            Confidence = confidence,
            Reason = $"Z-score: {zScore:F2}"
        };
    }

    private AnomalyDetectionResult DetectIQRAnomaly(double value, MetricBaseline baseline)
    {
        if (baseline.IQR == 0)
        {
            return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0 };
        }

        var lowerBound = baseline.Q1 - (1.5 * baseline.IQR);
        var upperBound = baseline.Q3 + (1.5 * baseline.IQR);

        if (value < lowerBound || value > upperBound)
        {
            var extremeLowerBound = baseline.Q1 - (3.0 * baseline.IQR);
            var extremeUpperBound = baseline.Q3 + (3.0 * baseline.IQR);

            var confidence = (value < extremeLowerBound || value > extremeUpperBound) ? 1.0 : 0.7;

            return new AnomalyDetectionResult
            {
                IsAnomaly = true,
                Confidence = confidence,
                Reason = $"IQR outlier: value {value:F2} outside bounds [{lowerBound:F2}, {upperBound:F2}]"
            };
        }

        return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0 };
    }

    private AnomalyDetectionResult DetectMLAnomaly(double value, MetricHistory history)
    {
        // Simplified ML-based anomaly detection using moving average and trend analysis
        var recentValues = history.DataPoints
            .TakeLast(20)
            .Select(dp => dp.Value)
            .ToArray();

        if (recentValues.Length < 10)
        {
            return new AnomalyDetectionResult { IsAnomaly = false, Confidence = 0.0 };
        }

        // Calculate trend and deviation from trend
        var movingAverage = recentValues.TakeLast(10).Average();
        var trendDeviation = Math.Abs(value - movingAverage) / movingAverage;

        // Check for sudden spikes or drops
        var recentChange = recentValues.Length > 1 ? Math.Abs(value - recentValues[^2]) / recentValues[^2] : 0;

        var confidence = trendDeviation > 0.5 ? 0.8 : (trendDeviation > 0.3 ? 0.6 : 0.0);
        confidence = Math.Max(confidence, recentChange > 1.0 ? 0.9 : 0.0);

        return new AnomalyDetectionResult
        {
            IsAnomaly = confidence > _options.SensitivityThreshold,
            Confidence = confidence,
            Reason = $"ML analysis: trend deviation {trendDeviation:F2}, recent change {recentChange:F2}"
        };
    }

    private async Task HandleDetectedAnomalyAsync(string metricName, double value, AnomalyDetectionResult result, Dictionary<string, string>? tags)
    {
        var alertKey = CreateAlertKey(metricName, tags);

        // Check cooldown period
        if (_alertCooldowns.TryGetValue(alertKey, out var lastAlert))
        {
            if (DateTimeOffset.UtcNow - lastAlert < TimeSpan.FromMinutes(_options.AlertCooldownMinutes))
            {
                return; // Still in cooldown
            }
        }

        var anomaly = new DetectedAnomaly
        {
            Id = Guid.NewGuid().ToString(),
            MetricName = metricName,
            Value = value,
            Timestamp = DateTimeOffset.UtcNow,
            Confidence = result.Confidence,
            Reason = result.Reason,
            Severity = DetermineSeverity(result.Confidence),
            Tags = tags ?? new Dictionary<string, string>(),
            Baseline = result.Baseline
        };

        _detectedAnomalies.Enqueue(anomaly);

        // Update cooldown
        _alertCooldowns.AddOrUpdate(alertKey, DateTimeOffset.UtcNow, (_, _) => DateTimeOffset.UtcNow);

        // Log the anomaly
        LogAnomalyDetected(_logger, metricName, value, result.Confidence, result.Reason, null);

        // Record telemetry
        using var activity = HonuaTelemetry.StartActivity(HonuaTelemetry.Activities.AnomalyDetection);
        activity?.SetTag("metric.name", metricName);
        activity?.SetTag("anomaly.confidence", result.Confidence);
        activity?.SetTag("anomaly.severity", anomaly.Severity);
    }

    private string DetermineSeverity(double confidence)
    {
        return confidence switch
        {
            >= 0.9 => "Critical",
            >= 0.7 => "High",
            >= 0.5 => "Medium",
            _ => "Low"
        };
    }

    private string CreateMetricKey(string metricName, Dictionary<string, string>? tags)
    {
        if (tags == null || tags.Count == 0)
        {
            return metricName;
        }

        var tagString = string.Join(",", tags.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{metricName}|{tagString}";
    }

    private string CreateAlertKey(string metricName, Dictionary<string, string>? tags)
    {
        return CreateMetricKey(metricName, tags);
    }

    private string GenerateAnomalyReason(double value, MetricBaseline baseline,
        AnomalyDetectionResult zScore, AnomalyDetectionResult iqr, AnomalyDetectionResult ml)
    {
        var reasons = new List<string>();

        if (zScore.Confidence > _options.SensitivityThreshold)
            reasons.Add(zScore.Reason);

        if (iqr.Confidence > _options.SensitivityThreshold)
            reasons.Add(iqr.Reason);

        if (ml.Confidence > _options.SensitivityThreshold)
            reasons.Add(ml.Reason);

        return string.Join("; ", reasons);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // No background processing required for this service
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // No background processing to stop
        return Task.CompletedTask;
    }
}

/// <summary>
/// Thread-safe metric history storage.
/// </summary>
internal sealed class MetricHistory
{
    private readonly Queue<MetricDataPoint> _dataPoints = new();
    private readonly object _lock = new();

    public string MetricName { get; }
    public Dictionary<string, string>? Tags { get; }

    public IEnumerable<MetricDataPoint> DataPoints
    {
        get
        {
            lock (_lock)
            {
                return _dataPoints.ToArray();
            }
        }
    }

    public MetricHistory(string metricName, Dictionary<string, string>? tags)
    {
        MetricName = metricName;
        Tags = tags;
    }

    public async Task AddDataPointAsync(MetricDataPoint dataPoint, int maxDataPoints)
    {
        lock (_lock)
        {
            _dataPoints.Enqueue(dataPoint);

            // Remove old data points if we exceed the limit
            while (_dataPoints.Count > maxDataPoints)
            {
                _dataPoints.Dequeue();
            }
        }
    }
}

/// <summary>
/// Represents a single metric data point.
/// </summary>
public sealed record MetricDataPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public double Value { get; init; }
}

/// <summary>
/// Result of anomaly detection analysis.
/// </summary>
public sealed record AnomalyDetectionResult
{
    public bool IsAnomaly { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public MetricBaseline? Baseline { get; init; }
    public Dictionary<string, double>? DetectionMethods { get; init; }
}

/// <summary>
/// Statistical baseline for a metric.
/// </summary>
public sealed record MetricBaseline
{
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public double Q1 { get; init; }
    public double Q3 { get; init; }
    public double IQR { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public int SampleSize { get; init; }
    public DateTimeOffset CalculatedAt { get; init; }
}

/// <summary>
/// Represents a detected anomaly.
/// </summary>
public sealed record DetectedAnomaly
{
    public string Id { get; init; } = string.Empty;
    public string MetricName { get; init; } = string.Empty;
    public double Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public Dictionary<string, string> Tags { get; init; } = new();
    public MetricBaseline? Baseline { get; init; }
}
