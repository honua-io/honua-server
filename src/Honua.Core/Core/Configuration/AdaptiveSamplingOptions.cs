using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public class AdaptiveSamplingOptions
{
    public const string SectionName = "AdaptiveSampling";

    [Range(0.0, 1.0)]
    public double DefaultSamplingRate { get; set; } = 0.01;

    [Range(0.0, 1.0)]
    public double MinSamplingRate { get; set; } = 0.001;

    [Range(0.0, 1.0)]
    public double MaxSamplingRate { get; set; } = 1.0;

    public bool EnableLoadBasedSampling { get; set; } = true;

    public bool EnableImportanceBasedSampling { get; set; } = true;

    [Range(10, 10000)]
    public int SampleSize { get; set; } = 1000;

    public TimeSpan TimeWindow { get; set; } = TimeSpan.FromMinutes(5);

    public bool Enabled { get; set; } = true;

    [Range(0.0, 1.0)]
    public double BaseSamplingRate { get; set; } = 0.01;

    public TimeSpan EvaluationWindow { get; set; } = TimeSpan.FromMinutes(1);

    public LoadSamplingOptions Load { get; set; } = new();

    public OperationSamplingOptions Operations { get; set; } = new();

    public ErrorSamplingOptions Error { get; set; } = new();
}

public class OperationSamplingOptions
{
    public bool Enabled { get; set; } = true;

    [Range(0.0, 1.0)]
    public double CriticalRate { get; set; } = 1.0;

    [Range(0.0, 1.0)]
    public double ImportantRate { get; set; } = 0.5;

    [Range(0.0, 1.0)]
    public double NormalRate { get; set; } = 0.1;

    [Range(0.0, 1.0)]
    public double BackgroundRate { get; set; } = 0.01;
}

public class ErrorSamplingOptions
{
    public bool Enabled { get; set; } = true;

    [Range(0.0, 1.0)]
    public double ErrorRate { get; set; } = 1.0;

    [Range(1, 60)]
    public int ErrorWindowMinutes { get; set; } = 5;

    [Range(0.0, 1.0)]
    public double ErrorRateThreshold { get; set; } = 0.05;

    [Range(1.0, 10.0)]
    public double ErrorMultiplier { get; set; } = 2.0;
}

public class LoadSamplingOptions
{
    public bool Enabled { get; set; } = true;

    [Range(0.0, 1.0)]
    public double LowLoadThreshold { get; set; } = 0.3;

    [Range(0.0, 1.0)]
    public double NormalLoadThreshold { get; set; } = 0.6;

    [Range(0.0, 1.0)]
    public double HighLoadThreshold { get; set; } = 0.8;

    [Range(0.0, 1.0)]
    public double CriticalLoadThreshold { get; set; } = 0.95;

    [Range(0.0, 1.0)]
    public double CpuThreshold { get; set; } = 0.7;

    [Range(0.0, 1.0)]
    public double MemoryThreshold { get; set; } = 0.8;

    [Range(1, 10000)]
    public int ActiveRequestThreshold { get; set; } = 100;

    [Range(1, 60000)]
    public int ResponseTimeThresholdMs { get; set; } = 1000;
}

public enum OperationImportance
{
    Background = 0,

    Low = 1,

    Normal = 2,

    Important = 3,

    High = 4,

    Critical = 5
}
