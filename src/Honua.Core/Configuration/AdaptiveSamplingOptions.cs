using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Configuration options for adaptive sampling behavior.
/// </summary>
public class AdaptiveSamplingOptions
{
    public const string SectionName = "AdaptiveSampling";

    /// <summary>
    /// Default sampling rate for operations (0.0 to 1.0).
    /// </summary>
    [Range(0.0, 1.0)]
    public double DefaultSamplingRate { get; set; } = 0.01;

    /// <summary>
    /// Minimum sampling rate that can be applied.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinSamplingRate { get; set; } = 0.001;

    /// <summary>
    /// Maximum sampling rate that can be applied.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MaxSamplingRate { get; set; } = 1.0;

    /// <summary>
    /// Whether to enable adaptive sampling based on system load.
    /// </summary>
    public bool EnableLoadBasedSampling { get; set; } = true;

    /// <summary>
    /// Whether to enable importance-based sampling.
    /// </summary>
    public bool EnableImportanceBasedSampling { get; set; } = true;

    /// <summary>
    /// Sample size for calculating adaptive rates.
    /// </summary>
    [Range(10, 10000)]
    public int SampleSize { get; set; } = 1000;

    /// <summary>
    /// Time window for sampling calculations.
    /// </summary>
    public TimeSpan TimeWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether adaptive sampling is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base sampling rate when not adaptive.
    /// </summary>
    [Range(0.0, 1.0)]
    public double BaseSamplingRate { get; set; } = 0.01;

    /// <summary>
    /// Evaluation window for adaptive adjustments.
    /// </summary>
    public TimeSpan EvaluationWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Load-based sampling configuration.
    /// </summary>
    public LoadSamplingOptions Load { get; set; } = new();

    /// <summary>
    /// Operation-specific sampling configuration.
    /// </summary>
    public OperationSamplingOptions Operations { get; set; } = new();

    /// <summary>
    /// Error handling sampling configuration.
    /// </summary>
    public ErrorSamplingOptions Error { get; set; } = new();
}

/// <summary>
/// Sampling configuration for different operation types.
/// </summary>
public class OperationSamplingOptions
{
    /// <summary>
    /// Whether operation-based sampling is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sampling rate for critical operations.
    /// </summary>
    [Range(0.0, 1.0)]
    public double CriticalRate { get; set; } = 1.0;

    /// <summary>
    /// Sampling rate for important operations.
    /// </summary>
    [Range(0.0, 1.0)]
    public double ImportantRate { get; set; } = 0.5;

    /// <summary>
    /// Sampling rate for normal operations.
    /// </summary>
    [Range(0.0, 1.0)]
    public double NormalRate { get; set; } = 0.1;

    /// <summary>
    /// Sampling rate for background operations.
    /// </summary>
    [Range(0.0, 1.0)]
    public double BackgroundRate { get; set; } = 0.01;
}

/// <summary>
/// Error sampling configuration.
/// </summary>
public class ErrorSamplingOptions
{
    /// <summary>
    /// Whether error-based sampling is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sampling rate for error conditions.
    /// </summary>
    [Range(0.0, 1.0)]
    public double ErrorRate { get; set; } = 1.0;

    /// <summary>
    /// Window in minutes for error tracking.
    /// </summary>
    [Range(1, 60)]
    public int ErrorWindowMinutes { get; set; } = 5;

    /// <summary>
    /// Error rate threshold for increasing sampling.
    /// </summary>
    [Range(0.0, 1.0)]
    public double ErrorRateThreshold { get; set; } = 0.05;

    /// <summary>
    /// Multiplier for sampling rate when errors exceed threshold.
    /// </summary>
    [Range(1.0, 10.0)]
    public double ErrorMultiplier { get; set; } = 2.0;
}

/// <summary>
/// Load-based sampling configuration.
/// </summary>
public class LoadSamplingOptions
{
    /// <summary>
    /// Whether load-based sampling is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// CPU load threshold for low load sampling.
    /// </summary>
    [Range(0.0, 1.0)]
    public double LowLoadThreshold { get; set; } = 0.3;

    /// <summary>
    /// CPU load threshold for normal load sampling.
    /// </summary>
    [Range(0.0, 1.0)]
    public double NormalLoadThreshold { get; set; } = 0.6;

    /// <summary>
    /// CPU load threshold for high load sampling.
    /// </summary>
    [Range(0.0, 1.0)]
    public double HighLoadThreshold { get; set; } = 0.8;

    /// <summary>
    /// CPU load threshold for critical load sampling.
    /// </summary>
    [Range(0.0, 1.0)]
    public double CriticalLoadThreshold { get; set; } = 0.95;

    /// <summary>
    /// General CPU threshold for load-based decisions.
    /// </summary>
    [Range(0.0, 1.0)]
    public double CpuThreshold { get; set; } = 0.7;

    /// <summary>
    /// Memory usage threshold for load-based decisions.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MemoryThreshold { get; set; } = 0.8;

    /// <summary>
    /// Active request count threshold for load-based decisions.
    /// </summary>
    [Range(1, 10000)]
    public int ActiveRequestThreshold { get; set; } = 100;

    /// <summary>
    /// Response time threshold in milliseconds for load-based decisions.
    /// </summary>
    [Range(1, 60000)]
    public int ResponseTimeThresholdMs { get; set; } = 1000;
}

/// <summary>
/// Importance level for operations, used in adaptive sampling.
/// </summary>
public enum OperationImportance
{
    /// <summary>
    /// Background operations that can be sampled lightly.
    /// </summary>
    Background = 0,

    /// <summary>
    /// Low importance operations that can be heavily sampled.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Normal importance operations with standard sampling.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// Important operations that should be sampled more than normal.
    /// </summary>
    Important = 3,

    /// <summary>
    /// High importance operations that should be sampled more frequently.
    /// </summary>
    High = 4,

    /// <summary>
    /// Critical operations that should always be sampled.
    /// </summary>
    Critical = 5
}