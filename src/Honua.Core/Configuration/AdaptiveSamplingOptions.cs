// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Adaptive sampling configuration for distributed tracing that automatically
/// adjusts sampling rates based on system load, error rates, and operation importance.
/// Provides intelligent tracing with minimal performance impact for self-hosted installations.
/// </summary>
public sealed class AdaptiveSamplingOptions
{
    /// <summary>
    /// Configuration section name for binding from environment variables.
    /// Maps to HONUA__ADAPTIVESAMPLING__* environment variables.
    /// </summary>
    public const string SectionName = "AdaptiveSampling";

    /// <summary>
    /// Enable adaptive sampling. When disabled, falls back to static sampling ratio.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__ENABLED
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Base sampling rate used as a starting point for adaptive adjustments.
    /// Range: 0.001-1.0 (0.1% to 100%). Default: 10%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__BASESAMPLRATE
    /// </summary>
    [Range(0.001, 1.0, ErrorMessage = "BaseSamplingRate must be between 0.001 and 1.0")]
    public double BaseSamplingRate { get; init; } = 0.1;

    /// <summary>
    /// Minimum sampling rate, even under high load conditions.
    /// Ensures critical operations are always traced.
    /// Range: 0.001-0.5 (0.1% to 50%). Default: 1%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__MINSAMPLRATE
    /// </summary>
    [Range(0.001, 0.5, ErrorMessage = "MinSamplingRate must be between 0.001 and 0.5")]
    public double MinSamplingRate { get; init; } = 0.01;

    /// <summary>
    /// Maximum sampling rate during low load or error conditions.
    /// Range: 0.1-1.0 (10% to 100%). Default: 50%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__MAXSAMPLRATE
    /// </summary>
    [Range(0.1, 1.0, ErrorMessage = "MaxSamplingRate must be between 0.1 and 1.0")]
    public double MaxSamplingRate { get; init; } = 0.5;

    /// <summary>
    /// Time window for collecting metrics to adjust sampling rates.
    /// Range: 30 seconds to 10 minutes. Default: 2 minutes.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__EVALUATIONWINDOWSECONDS
    /// </summary>
    public TimeSpan EvaluationWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// System load thresholds that trigger sampling rate adjustments.
    /// </summary>
    public LoadThresholds Load { get; init; } = new();

    /// <summary>
    /// Error rate thresholds that increase sampling for debugging.
    /// </summary>
    public ErrorThresholds Error { get; init; } = new();

    /// <summary>
    /// Per-operation sampling rates for different operation types.
    /// </summary>
    public OperationSampling Operations { get; init; } = new();
}

/// <summary>
/// System load thresholds for adaptive sampling adjustments.
/// Higher load reduces sampling to preserve performance.
/// </summary>
public sealed class LoadThresholds
{
    /// <summary>
    /// CPU usage percentage threshold for reducing sampling.
    /// Range: 30-95%. Default: 70%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__LOAD__CPUTHRESHOLD
    /// </summary>
    [Range(30, 95, ErrorMessage = "CpuThreshold must be between 30 and 95")]
    public double CpuThreshold { get; init; } = 70.0;

    /// <summary>
    /// Memory usage percentage threshold for reducing sampling.
    /// Range: 30-95%. Default: 80%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__LOAD__MEMORYTHRESHOLD
    /// </summary>
    [Range(30, 95, ErrorMessage = "MemoryThreshold must be between 30 and 95")]
    public double MemoryThreshold { get; init; } = 80.0;

    /// <summary>
    /// Active request count threshold for reducing sampling.
    /// Range: 10-1000. Default: 50.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__LOAD__ACTIVEREQUESTTHRESHOLD
    /// </summary>
    [Range(10, 1000, ErrorMessage = "ActiveRequestThreshold must be between 10 and 1000")]
    public int ActiveRequestThreshold { get; init; } = 50;

    /// <summary>
    /// Average response time in milliseconds that triggers load-based reduction.
    /// Range: 100-10000ms. Default: 1000ms (1 second).
    /// Environment variable: HONUA__ADAPTIVESAMPLING__LOAD__RESPONSETIMETHRESHOLDMS
    /// </summary>
    [Range(100, 10000, ErrorMessage = "ResponseTimeThresholdMs must be between 100 and 10000")]
    public int ResponseTimeThresholdMs { get; init; } = 1000;
}

/// <summary>
/// Error rate thresholds that trigger increased sampling for debugging.
/// Higher error rates increase sampling to capture more diagnostic information.
/// </summary>
public sealed class ErrorThresholds
{
    /// <summary>
    /// Error rate percentage that triggers increased sampling.
    /// Range: 0.1-50%. Default: 5%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__ERROR__ERRORRATETHRESHOLD
    /// </summary>
    [Range(0.1, 50.0, ErrorMessage = "ErrorRateThreshold must be between 0.1 and 50")]
    public double ErrorRateThreshold { get; init; } = 5.0;

    /// <summary>
    /// Multiplier applied to sampling rate when error threshold is exceeded.
    /// Range: 1.5-10.0. Default: 3.0 (triple the sampling rate).
    /// Environment variable: HONUA__ADAPTIVESAMPLING__ERROR__ERRORMULTIPLIER
    /// </summary>
    [Range(1.5, 10.0, ErrorMessage = "ErrorMultiplier must be between 1.5 and 10")]
    public double ErrorMultiplier { get; init; } = 3.0;

    /// <summary>
    /// Time window for calculating error rates.
    /// Range: 1-30 minutes. Default: 5 minutes.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__ERROR__ERRORWINDOWMINUTES
    /// </summary>
    [Range(1, 30, ErrorMessage = "ErrorWindowMinutes must be between 1 and 30")]
    public int ErrorWindowMinutes { get; init; } = 5;
}

/// <summary>
/// Per-operation sampling configuration for different types of operations.
/// Allows fine-tuning sampling rates based on operation importance.
/// </summary>
public sealed class OperationSampling
{
    /// <summary>
    /// Sampling rate for critical operations (auth, data writes).
    /// Range: 0.1-1.0. Default: 100% (always sample).
    /// Environment variable: HONUA__ADAPTIVESAMPLING__OPERATIONS__CRITICALRATE
    /// </summary>
    [Range(0.1, 1.0, ErrorMessage = "CriticalRate must be between 0.1 and 1.0")]
    public double CriticalRate { get; init; } = 1.0;

    /// <summary>
    /// Sampling rate for important operations (complex spatial queries).
    /// Range: 0.05-1.0. Default: 50%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__OPERATIONS__IMPORTANTRATE
    /// </summary>
    [Range(0.05, 1.0, ErrorMessage = "ImportantRate must be between 0.05 and 1.0")]
    public double ImportantRate { get; init; } = 0.5;

    /// <summary>
    /// Sampling rate for normal operations (standard queries).
    /// Range: 0.01-1.0. Default: 10%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__OPERATIONS__NORMALRATE
    /// </summary>
    [Range(0.01, 1.0, ErrorMessage = "NormalRate must be between 0.01 and 1.0")]
    public double NormalRate { get; init; } = 0.1;

    /// <summary>
    /// Sampling rate for background operations (health checks, metrics).
    /// Range: 0.001-0.1. Default: 1%.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__OPERATIONS__BACKGROUNDRATE
    /// </summary>
    [Range(0.001, 0.1, ErrorMessage = "BackgroundRate must be between 0.001 and 0.1")]
    public double BackgroundRate { get; init; } = 0.01;

    /// <summary>
    /// Enable operation-specific sampling rates. When disabled, uses base rate for all.
    /// Environment variable: HONUA__ADAPTIVESAMPLING__OPERATIONS__ENABLED
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Operation importance classification for sampling decisions.
/// </summary>
public enum OperationImportance
{
    /// <summary>
    /// Background operations like health checks and metrics collection.
    /// Lowest sampling priority.
    /// </summary>
    Background = 0,

    /// <summary>
    /// Standard read operations and basic queries.
    /// Normal sampling priority.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Complex operations like spatial queries and bulk processing.
    /// Higher sampling priority.
    /// </summary>
    Important = 2,

    /// <summary>
    /// Critical operations like authentication, data writes, and error handling.
    /// Highest sampling priority, typically always sampled.
    /// </summary>
    Critical = 3
}
