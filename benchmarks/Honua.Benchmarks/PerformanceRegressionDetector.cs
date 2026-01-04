// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using BenchmarkDotNet.Reports;

namespace Honua.Benchmarks;

/// <summary>
/// Performance regression detection system that:
/// - Compares current performance against established baselines
/// - Detects performance regressions with statistical significance
/// - Generates CI-friendly reports and exit codes
/// - Maintains historical performance data
/// - Provides alerts for performance degradation
///
/// Regression detection thresholds:
/// - Critical regression: &gt;25% performance degradation
/// - Warning regression: &gt;10% performance degradation
/// - Statistical significance: p &lt; 0.05 with &gt;=5 samples
/// - Memory regression: &gt;20% increase in allocations
/// </summary>
public class PerformanceRegressionDetector
{
    private readonly PerformanceBaseline _baseline;
    private readonly PerformanceThresholds _thresholds;
    private readonly string _baselineFilePath;

    public PerformanceRegressionDetector(string? baselineFilePath = null)
    {
        _baselineFilePath = baselineFilePath ?? Path.Combine(Directory.GetCurrentDirectory(), "performance-baseline.json");
        _baseline = LoadBaseline();
        _thresholds = new PerformanceThresholds
        {
            CriticalRegressionThreshold = 0.25, // 25%
            WarningRegressionThreshold = 0.10,   // 10%
            MemoryRegressionThreshold = 0.20,    // 20%
            MinSamplesForSignificance = 5,
            SignificanceLevel = 0.05            // p < 0.05
        };
    }

    /// <summary>
    /// Analyze benchmark results and detect performance regressions
    /// </summary>
    public RegressionAnalysisResult AnalyzeResults(Summary benchmarkSummary)
    {
        var result = new RegressionAnalysisResult
        {
            Timestamp = DateTime.UtcNow,
            TotalBenchmarks = benchmarkSummary.Reports.Length
        };

        foreach (var report in benchmarkSummary.Reports)
        {
            var benchmarkName = report.BenchmarkCase.Descriptor.DisplayInfo;
            var currentMetrics = ExtractMetrics(report);

            if (_baseline.Benchmarks.TryGetValue(benchmarkName, out var baselineMetrics))
            {
                var regression = DetectRegression(benchmarkName, baselineMetrics, currentMetrics);
                result.Regressions.Add(regression);

                // Update severity counts
                switch (regression.Severity)
                {
                    case RegressionSeverity.Critical:
                        result.CriticalRegressions++;
                        break;
                    case RegressionSeverity.Warning:
                        result.WarningRegressions++;
                        break;
                    case RegressionSeverity.Improvement:
                        result.Improvements++;
                        break;
                }
            }
            else
            {
                // New benchmark - add to baseline for future comparisons
                result.NewBenchmarks++;
                _baseline.Benchmarks[benchmarkName] = currentMetrics;
            }
        }

        // Determine overall status
        result.OverallStatus = DetermineOverallStatus(result);

        return result;
    }

    /// <summary>
    /// Update baseline with current results (typically after accepting performance changes)
    /// </summary>
    public void UpdateBaseline(Summary benchmarkSummary, string reason)
    {
        foreach (var report in benchmarkSummary.Reports)
        {
            var benchmarkName = report.BenchmarkCase.Descriptor.DisplayInfo;
            var metrics = ExtractMetrics(report);
            _baseline.Benchmarks[benchmarkName] = metrics;
        }

        _baseline.LastUpdated = DateTime.UtcNow;
        _baseline.UpdateReason = reason;
        _baseline.Version++;

        SaveBaseline();
    }

    /// <summary>
    /// Generate CI-friendly report
    /// </summary>
    public string GenerateCiReport(RegressionAnalysisResult result)
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine("# Performance Benchmark Results");
        report.AppendLine();
        report.AppendLine($"**Overall Status:** {GetStatusEmoji(result.OverallStatus)} {result.OverallStatus}");
        report.AppendLine($"**Timestamp:** {result.Timestamp:yyyy-MM-dd HH:mm:ss UTC}");
        report.AppendLine();

        // Summary statistics
        report.AppendLine("## Summary");
        report.AppendLine($"- Total Benchmarks: {result.TotalBenchmarks}");
        report.AppendLine($"- Critical Regressions: {result.CriticalRegressions}");
        report.AppendLine($"- Warnings: {result.WarningRegressions}");
        report.AppendLine($"- Improvements: {result.Improvements}");
        report.AppendLine($"- New Benchmarks: {result.NewBenchmarks}");
        report.AppendLine();

        // Critical regressions (blocking)
        var criticalRegressions = result.Regressions.Where(r =>
        r.Severity == RegressionSeverity.Critical).ToList();
        if (criticalRegressions.Any())
        {
            report.AppendLine("## 🚨 Critical Performance Regressions");
            report.AppendLine();
            foreach (var regression in criticalRegressions)
            {
                report.AppendLine($"### {regression.BenchmarkName}");
                report.AppendLine($"- **Performance Change:** {regression.PerformanceChange:P2} slower");
                report.AppendLine($"- **Current:** {regression.CurrentMetrics.MedianNanoseconds:N0} ns");
                report.AppendLine($"- **Baseline:** {regression.BaselineMetrics.MedianNanoseconds:N0} ns");

                if (regression.MemoryRegression.HasValue)
                {
                    report.AppendLine($"- **Memory Change:** {regression.MemoryRegression:P2} more allocations");
                }

                report.AppendLine($"- **Statistical Significance:** p = {regression.StatisticalSignificance:F4}");
                report.AppendLine();
            }
        }

        // Warnings
        var warnings = result.Regressions.Where(r =>
        r.Severity == RegressionSeverity.Warning).ToList();
        if (warnings.Any())
        {
            report.AppendLine("## ⚠️ Performance Warnings");
            report.AppendLine();
            foreach (var warning in warnings)
            {
                report.AppendLine($"- **{warning.BenchmarkName}:** {warning.PerformanceChange:P2} slower (p = {warning.StatisticalSignificance:F4})");
            }
            report.AppendLine();
        }

        // Improvements
        var improvements = result.Regressions.Where(r =>
        r.Severity == RegressionSeverity.Improvement).ToList();
        if (improvements.Any())
        {
            report.AppendLine("## 🎉 Performance Improvements");
            report.AppendLine();
            foreach (var improvement in improvements)
            {
                report.AppendLine($"- **{improvement.BenchmarkName}:** {Math.Abs(improvement.PerformanceChange):P2} faster");
            }
            report.AppendLine();
        }

        // Recommendations
        if (result.OverallStatus != RegressionStatus.Passed)
        {
            report.AppendLine("## 📋 Recommendations");
            report.AppendLine();

            if (criticalRegressions.Any())
            {
                report.AppendLine("### Critical Actions Required");
                report.AppendLine("- Review and optimize the affected benchmarks before merging");
                report.AppendLine("- Consider if the performance change is acceptable for the new functionality");
                report.AppendLine("- Update performance baseline if the regression is intentional");
                report.AppendLine();
            }

            if (warnings.Any())
            {
                report.AppendLine("### Warning Actions");
                report.AppendLine("- Monitor these benchmarks in future builds");
                report.AppendLine("- Consider optimization if the trend continues");
                report.AppendLine();
            }
        }

        // CI Integration instructions
        report.AppendLine("## CI Integration");
        report.AppendLine();
        report.AppendLine("To update the performance baseline after reviewing changes:");
        report.AppendLine("```bash");
        report.AppendLine("dotnet run --project benchmarks/Honua.Benchmarks -- --update-baseline \"Reason for update\"");
        report.AppendLine("```");

        return report.ToString();
    }

    /// <summary>
    /// Get exit code for CI integration
    /// </summary>
    public int GetExitCode(RegressionAnalysisResult result)
    {
        return result.OverallStatus switch
        {
            RegressionStatus.Passed => 0,
            RegressionStatus.Warning => (object *)1,
            RegressionStatus.Failed => (object *)2,
            _ => (object *)1
        };
    }

    private BenchmarkMetrics ExtractMetrics(BenchmarkReport report)
    {
        var statistics = report.ResultStatistics;
        var measurements = report.AllMeasurements;

        return new BenchmarkMetrics
        {
            MedianNanoseconds = statistics?.Median ?? 0,
            MeanNanoseconds = statistics?.Mean ?? 0,
            StandardDeviationNanoseconds = statistics?.StandardDeviation ?? 0,
            MinNanoseconds = statistics?.Min ?? 0,
            MaxNanoseconds = statistics?.Max ?? 0,
            SampleCount = measurements.Count(),
            AllocatedBytesPerOperation = report.GcStats.BytesAllocatedPerOperation,
            Gen0CollectionsPerOperation = report.GcStats.Gen0Collections,
            Gen1CollectionsPerOperation = report.GcStats.Gen1Collections,
            Gen2CollectionsPerOperation = report.GcStats.Gen2Collections,
            Timestamp = DateTime.UtcNow
        };
    }

    private BenchmarkRegression DetectRegression(string benchmarkName, BenchmarkMetrics baseline, BenchmarkMetrics current)
    {
        var performanceChange = (current.MedianNanoseconds - baseline.MedianNanoseconds) / baseline.MedianNanoseconds;
        var statisticalSignificance = CalculateStatisticalSignificance(baseline, current);

        var regression = new BenchmarkRegression
        {
            BenchmarkName = benchmarkName,
            BaselineMetrics = baseline,
            CurrentMetrics = current,
            PerformanceChange = performanceChange,
            StatisticalSignificance = statisticalSignificance
        };

        // Check for memory regression
        if (baseline.AllocatedBytesPerOperation >
        0 && current.AllocatedBytesPerOperation >
        0)
        {
            var memoryChange = (current.AllocatedBytesPerOperation - baseline.AllocatedBytesPerOperation) / baseline.AllocatedBytesPerOperation;
            if (memoryChange >
            _thresholds.MemoryRegressionThreshold)
            {
                regression.MemoryRegression = memoryChange;
            }
        }

        // Determine severity
        if (statisticalSignificance <
        _thresholds.SignificanceLevel &&
            Math.Min(baseline.SampleCount, current.SampleCount) >= _thresholds.MinSamplesForSignificance)
        {
            if (performanceChange >
            _thresholds.CriticalRegressionThreshold || regression.MemoryRegression >
            _thresholds.MemoryRegressionThreshold)
            {
                regression.Severity = RegressionSeverity.Critical;
            }
            else if (performanceChange >
            _thresholds.WarningRegressionThreshold)
            {
                regression.Severity = RegressionSeverity.Warning;
            }
            else if (performanceChange <
            -_thresholds.WarningRegressionThreshold) // Improvement
            {
                regression.Severity = RegressionSeverity.Improvement;
            }
            else
            {
                regression.Severity = RegressionSeverity.None;
            }
        }
        else
        {
            regression.Severity = RegressionSeverity.None; // Not statistically significant
        }

        return regression;
    }

    private double CalculateStatisticalSignificance(BenchmarkMetrics baseline, BenchmarkMetrics current)
    {
        // Simplified t-test for demonstration
        // In production, use proper statistical libraries
        var pooledStdDev = Math.Sqrt((Math.Pow(baseline.StandardDeviationNanoseconds, 2) + Math.Pow(current.StandardDeviationNanoseconds, 2)) / 2);

        if (pooledStdDev == 0)
            return 1.0; // No variation, not significant

        var standardError = pooledStdDev * Math.Sqrt(2.0 / Math.Min(baseline.SampleCount, current.SampleCount));
        var tStat = Math.Abs(current.MeanNanoseconds - baseline.MeanNanoseconds) / standardError;

        // Approximate p-value for t-distribution (simplified)
        // This is a rough approximation - use proper statistical libraries in production
        if (tStat >
        2.576) return 0.01;   // p < 0.01
        if (tStat >
        1.960) return 0.05;   // p < 0.05
        if (tStat >
        1.645) return 0.10;   // p < 0.10
        return 0.20; // p >= 0.20
    }

    private RegressionStatus DetermineOverallStatus(RegressionAnalysisResult result)
    {
        if (result.CriticalRegressions >
        0)
            return RegressionStatus.Failed;

        if (result.WarningRegressions >
        0)
            return RegressionStatus.Warning;

        return RegressionStatus.Passed;
    }

    private PerformanceBaseline LoadBaseline()
    {
        if (File.Exists(_baselineFilePath))
        {
            try
            {
                var json = File.ReadAllText(_baselineFilePath);
                return JsonSerializer.Deserialize <
                PerformanceBaseline >
                (json) ?? new PerformanceBaseline();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not load baseline from {_baselineFilePath}: {ex.Message}");
                Console.WriteLine("Starting with empty baseline.");
            }
        }

        return new PerformanceBaseline();
    }

    private void SaveBaseline()
    {
        try
        {
            var json = JsonSerializer.Serialize(_baseline, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_baselineFilePath, json);
            Console.WriteLine($"Baseline updated: {_baselineFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving baseline: {ex.Message}");
        }
    }

    private static string GetStatusEmoji(RegressionStatus status) => status switch
    {
        RegressionStatus.Passed => "✅",
        RegressionStatus.Warning => "⚠️",
        RegressionStatus.Failed => "❌",
        _ => "❓"
    };
}

/// <summary>
/// Performance baseline data structure
/// </summary>
public class PerformanceBaseline
{
    public int Version { get; set; } = 1;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string UpdateReason { get; set; } = "Initial baseline";
    public Dictionary<string, BenchmarkMetrics> Benchmarks { get; set; } = new();
}

/// <summary>
/// Metrics for a single benchmark
/// </summary>
public class BenchmarkMetrics
{
    public double MedianNanoseconds { get; set; }
    public double MeanNanoseconds { get; set; }
    public double StandardDeviationNanoseconds { get; set; }
    public double MinNanoseconds { get; set; }
    public double MaxNanoseconds { get; set; }
    public int SampleCount { get; set; }
    public long AllocatedBytesPerOperation { get; set; }
    public int Gen0CollectionsPerOperation { get; set; }
    public int Gen1CollectionsPerOperation { get; set; }
    public int Gen2CollectionsPerOperation { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Regression detection thresholds
/// </summary>
public class PerformanceThresholds
{
    public double CriticalRegressionThreshold { get; set; }
    public double WarningRegressionThreshold { get; set; }
    public double MemoryRegressionThreshold { get; set; }
    public int MinSamplesForSignificance { get; set; }
    public double SignificanceLevel { get; set; }
}

/// <summary>
/// Analysis result for performance regression detection
/// </summary>
public class RegressionAnalysisResult
{
    public DateTime Timestamp { get; set; }
    public RegressionStatus OverallStatus { get; set; }
    public int TotalBenchmarks { get; set; }
    public int CriticalRegressions { get; set; }
    public int WarningRegressions { get; set; }
    public int Improvements { get; set; }
    public int NewBenchmarks { get; set; }
    public List<BenchmarkRegression> Regressions { get; set; } = new();
}

/// <summary>
/// Individual benchmark regression data
/// </summary>
public class BenchmarkRegression
{
    public string BenchmarkName { get; set; } = string.Empty;
    public BenchmarkMetrics BaselineMetrics { get; set; } = null!;
    public BenchmarkMetrics CurrentMetrics { get; set; } = null!;
    public double PerformanceChange { get; set; }
    public double? MemoryRegression { get; set; }
    public double StatisticalSignificance { get; set; }
    public RegressionSeverity Severity { get; set; }
}

/// <summary>
/// Regression severity levels
/// </summary>
public enum RegressionSeverity
{
    None,
    Improvement,
    Warning,
    Critical
}

/// <summary>
/// Overall regression status
/// </summary>
public enum RegressionStatus
{
    Passed,
    Warning,
    Failed
}
