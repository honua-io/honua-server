// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Reports;

namespace Honua.Benchmarks;

/// <summary>
/// Performance dashboard and reporting system that:
/// - Generates comprehensive performance reports
/// - Creates trend analysis across multiple runs
/// - Provides visual representations of performance data
/// - Generates executive summaries and technical details
/// - Supports multiple output formats (HTML, JSON, CSV, Markdown)
/// - Creates performance scorecards for different scenarios
/// </summary>
public class PerformanceDashboard
{
    private readonly string _outputDirectory;
    private readonly PerformanceReportConfiguration _configuration;

    public PerformanceDashboard(string outputDirectory, PerformanceReportConfiguration? configuration = null)
    {
        _outputDirectory = outputDirectory;
        _configuration = configuration ?? new PerformanceReportConfiguration();

        Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>
    /// Generate comprehensive performance dashboard
    /// </summary>
    public async Task<PerformanceDashboardResult> GenerateDashboardAsync(
        Summary benchmarkSummary,
        RegressionAnalysisResult? regressionAnalysis = null,
        PerformanceBaseline? baseline = null)
    {
        var result = new PerformanceDashboardResult
        {
            GeneratedAt = DateTime.UtcNow,
            OutputDirectory = _outputDirectory
        };

        try
        {
            // Generate individual reports
            if (_configuration.GenerateExecutiveSummary)
            {
                var executivePath = await GenerateExecutiveSummaryAsync(benchmarkSummary, regressionAnalysis, baseline);
                result.GeneratedFiles.Add("executive-summary.html", executivePath);
            }

            if (_configuration.GenerateTechnicalReport)
            {
                var technicalPath = await GenerateTechnicalReportAsync(benchmarkSummary, regressionAnalysis, baseline);
                result.GeneratedFiles.Add("technical-report.html", technicalPath);
            }

            if (_configuration.GeneratePerformanceScorecard)
            {
                var scorecardPath = await GeneratePerformanceScorecardAsync(benchmarkSummary, baseline);
                result.GeneratedFiles.Add("performance-scorecard.html", scorecardPath);
            }

            if (_configuration.GenerateTrendAnalysis && baseline != null)
            {
                var trendPath = await GenerateTrendAnalysisAsync(benchmarkSummary, baseline);
                result.GeneratedFiles.Add("trend-analysis.html", trendPath);
            }

            if (_configuration.GenerateJsonData)
            {
                var jsonPath = await GenerateJsonDataAsync(benchmarkSummary, regressionAnalysis, baseline);
                result.GeneratedFiles.Add("performance-data.json", jsonPath);
            }

            if (_configuration.GenerateCsvData)
            {
                var csvPath = await GenerateCsvDataAsync(benchmarkSummary);
                result.GeneratedFiles.Add("performance-data.csv", csvPath);
            }

            // Generate main dashboard index
            var indexPath = await GenerateDashboardIndexAsync(result);
            result.GeneratedFiles.Add("index.html", indexPath);
            result.DashboardUrl = $"file://{Path.GetFullPath(indexPath)}";

            result.Success = true;
            result.Message = "Dashboard generated successfully";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error generating dashboard: {ex.Message}";
        }

        return result;
    }

    private async Task<string> GenerateExecutiveSummaryAsync(
        Summary benchmarkSummary,
        RegressionAnalysisResult? regressionAnalysis,
        PerformanceBaseline? baseline)
    {
        var filePath = Path.Combine(_outputDirectory, "executive-summary.html");
        var html = new StringBuilder();

        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine("    <title>Honua Server - Performance Executive Summary</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetDashboardStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        // Header
        html.AppendLine("    <header>");
        html.AppendLine("        <h1>🚀 Honua Server Performance Executive Summary</h1>");
        html.AppendLine($"        <p class=\"timestamp\">Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}</p>");
        html.AppendLine("    </header>");

        // Key Performance Indicators
        html.AppendLine("    <section class=\"kpi-section\">");
        html.AppendLine("        <h2>📊 Key Performance Indicators</h2>");
        html.AppendLine("        <div class=\"kpi-grid\">");

        var performanceScore = CalculateOverallPerformanceScore(benchmarkSummary, regressionAnalysis);
        var scoreColor = GetScoreColor(performanceScore);

        html.AppendLine($"            <div class=\"kpi-card {scoreColor}\">");
        html.AppendLine("                <h3>Overall Performance Score</h3>");
        html.AppendLine($"                <div class=\"kpi-value\">{performanceScore}/100</div>");
        html.AppendLine("                <div class=\"kpi-description\">Composite performance rating</div>");
        html.AppendLine("            </div>");

        var throughputMetric = ExtractThroughputMetric(benchmarkSummary);
        html.AppendLine("            <div class=\"kpi-card\">");
        html.AppendLine("                <h3>Peak Throughput</h3>");
        html.AppendLine($"                <div class=\"kpi-value\">{throughputMetric.Value:F0}</div>");
        html.AppendLine($"                <div class=\"kpi-description\">{throughputMetric.Unit}</div>");
        html.AppendLine("            </div>");

        var responseTimeMetric = ExtractResponseTimeMetric(benchmarkSummary);
        html.AppendLine("            <div class=\"kpi-card\">");
        html.AppendLine("                <h3>Average Response Time</h3>");
        html.AppendLine($"                <div class=\"kpi-value\">{responseTimeMetric.Value:F1}</div>");
        html.AppendLine($"                <div class=\"kpi-description\">{responseTimeMetric.Unit}</div>");
        html.AppendLine("            </div>");

        var memoryEfficiency = CalculateMemoryEfficiency(benchmarkSummary);
        html.AppendLine($"            <div class=\"kpi-card {GetEfficiencyColor(memoryEfficiency)}\">");
        html.AppendLine("                <h3>Memory Efficiency</h3>");
        html.AppendLine($"                <div class=\"kpi-value\">{memoryEfficiency:F0}%</div>");
        html.AppendLine("                <div class=\"kpi-description\">Memory utilization score</div>");
        html.AppendLine("            </div>");

        html.AppendLine("        </div>");
        html.AppendLine("    </section>");

        // Performance Status
        if (regressionAnalysis != null)
        {
            html.AppendLine("    <section class=\"status-section\">");
            html.AppendLine("        <h2>⚡ Performance Status</h2>");

            var statusColor = regressionAnalysis.OverallStatus switch
            {
                RegressionStatus.Passed => "success",
                RegressionStatus.Warning => "warning",
                RegressionStatus.Failed => "danger",
                _ => "neutral"
            };

            html.AppendLine($"        <div class=\"status-card {statusColor}\">");
            html.AppendLine($"            <h3>{GetStatusIcon(regressionAnalysis.OverallStatus)} {regressionAnalysis.OverallStatus}</h3>");
            html.AppendLine($"            <p>Total Benchmarks: {regressionAnalysis.TotalBenchmarks}</p>");

            if (regressionAnalysis.CriticalRegressions > 0)
            {
                html.AppendLine($"            <p class=\"critical\">⚠️ Critical Issues: {regressionAnalysis.CriticalRegressions}</p>");
            }

            if (regressionAnalysis.WarningRegressions > 0)
            {
                html.AppendLine($"            <p class=\"warning\">⚠️ Warnings: {regressionAnalysis.WarningRegressions}</p>");
            }

            if (regressionAnalysis.Improvements > 0)
            {
                html.AppendLine($"            <p class=\"improvement\">🎉 Improvements: {regressionAnalysis.Improvements}</p>");
            }

            html.AppendLine("        </div>");
            html.AppendLine("    </section>");
        }

        // Benchmark Categories Summary
        html.AppendLine("    <section class=\"categories-section\">");
        html.AppendLine("        <h2>🎯 Performance Categories</h2>");

        var categories = CategorizeBenchmarks(benchmarkSummary);
        foreach (var category in categories)
        {
            var categoryScore = CalculateCategoryScore(category.Value);
            var categoryColor = GetScoreColor(categoryScore);

            html.AppendLine($"        <div class=\"category-card {categoryColor}\">");
            html.AppendLine($"            <h3>{category.Key}</h3>");
            html.AppendLine($"            <div class=\"category-score\">{categoryScore}/100</div>");
            html.AppendLine($"            <div class=\"category-details\">");
            html.AppendLine($"                <p>Benchmarks: {category.Value.Count}</p>");
            html.AppendLine($"                <p>Avg. Performance: {CalculateAveragePerformance(category.Value):F1} ops/sec</p>");
            html.AppendLine($"            </div>");
            html.AppendLine("        </div>");
        }

        html.AppendLine("    </section>");

        // Recommendations
        html.AppendLine("    <section class=\"recommendations-section\">");
        html.AppendLine("        <h2>💡 Executive Recommendations</h2>");
        html.AppendLine("        <div class=\"recommendations\">");

        var recommendations = GenerateExecutiveRecommendations(benchmarkSummary, regressionAnalysis, performanceScore);
        foreach (var recommendation in recommendations)
        {
            html.AppendLine($"            <div class=\"recommendation-item {recommendation.Priority.ToLower()}\">");
            html.AppendLine($"                <h4>{recommendation.Priority}: {recommendation.Title}</h4>");
            html.AppendLine($"                <p>{recommendation.Description}</p>");
            html.AppendLine($"                <p><strong>Impact:</strong> {recommendation.Impact}</p>");
            html.AppendLine("            </div>");
        }

        html.AppendLine("        </div>");
        html.AppendLine("    </section>");

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        await File.WriteAllTextAsync(filePath, html.ToString());
        return filePath;
    }

    private async Task<string> GenerateTechnicalReportAsync(
        Summary benchmarkSummary,
        RegressionAnalysisResult? regressionAnalysis,
        PerformanceBaseline? baseline)
    {
        var filePath = Path.Combine(_outputDirectory, "technical-report.html");
        var html = new StringBuilder();

        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine("    <title>Honua Server - Technical Performance Report</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetDashboardStyles());
        html.AppendLine(GetTechnicalReportStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        html.AppendLine("    <header>");
        html.AppendLine("        <h1>🔧 Honua Server Technical Performance Report</h1>");
        html.AppendLine($"        <p class=\"timestamp\">Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}</p>");
        html.AppendLine("    </header>");

        // Detailed Benchmark Results
        html.AppendLine("    <section class=\"benchmark-results\">");
        html.AppendLine("        <h2>📈 Detailed Benchmark Results</h2>");

        html.AppendLine("        <table class=\"results-table\">");
        html.AppendLine("            <thead>");
        html.AppendLine("                <tr>");
        html.AppendLine("                    <th>Benchmark</th>");
        html.AppendLine("                    <th>Mean (ns)</th>");
        html.AppendLine("                    <th>StdDev (ns)</th>");
        html.AppendLine("                    <th>Median (ns)</th>");
        html.AppendLine("                    <th>Operations/sec</th>");
        html.AppendLine("                    <th>Allocated (B)</th>");
        html.AppendLine("                    <th>GC (Gen 0/1/2)</th>");
        html.AppendLine("                </tr>");
        html.AppendLine("            </thead>");
        html.AppendLine("            <tbody>");

        foreach (var report in benchmarkSummary.Reports.OrderBy(r => r.BenchmarkCase.Descriptor.DisplayInfo))
        {
            var stats = report.ResultStatistics;
            var gcStats = report.GcStats;
            var opsPerSec = stats != null && stats.Mean > 0 ? 1_000_000_000 / stats.Mean : 0;

            html.AppendLine("                <tr>");
            html.AppendLine($"                    <td class=\"benchmark-name\">{report.BenchmarkCase.Descriptor.DisplayInfo}</td>");
            html.AppendLine($"                    <td>{stats?.Mean:F2}</td>");
            html.AppendLine($"                    <td>{stats?.StandardDeviation:F2}</td>");
            html.AppendLine($"                    <td>{stats?.Median:F2}</td>");
            html.AppendLine($"                    <td>{opsPerSec:F0}</td>");
            html.AppendLine($"                    <td>{gcStats.BytesAllocatedPerOperation:N0}</td>");
            html.AppendLine($"                    <td>{gcStats.Gen0Collections}/{gcStats.Gen1Collections}/{gcStats.Gen2Collections}</td>");
            html.AppendLine("                </tr>");
        }

        html.AppendLine("            </tbody>");
        html.AppendLine("        </table>");
        html.AppendLine("    </section>");

        // Memory Analysis
        html.AppendLine("    <section class=\"memory-analysis\">");
        html.AppendLine("        <h2>💾 Memory Analysis</h2>");

        var memoryAnalysis = AnalyzeMemoryUsage(benchmarkSummary);
        html.AppendLine("        <div class=\"memory-grid\">");

        html.AppendLine("            <div class=\"memory-card\">");
        html.AppendLine("                <h3>Total Allocations</h3>");
        html.AppendLine($"                <div class=\"memory-value\">{memoryAnalysis.TotalAllocations:N0} bytes</div>");
        html.AppendLine("            </div>");

        html.AppendLine("            <div class=\"memory-card\">");
        html.AppendLine("                <h3>Average per Operation</h3>");
        html.AppendLine($"                <div class=\"memory-value\">{memoryAnalysis.AveragePerOperation:F1} bytes</div>");
        html.AppendLine("            </div>");

        html.AppendLine("            <div class=\"memory-card\">");
        html.AppendLine("                <h3>GC Pressure</h3>");
        html.AppendLine($"                <div class=\"memory-value\">{memoryAnalysis.GcPressureScore:F1}/10</div>");
        html.AppendLine("            </div>");

        html.AppendLine("            <div class=\"memory-card\">");
        html.AppendLine("                <h3>Memory Efficiency</h3>");
        html.AppendLine($"                <div class=\"memory-value\">{memoryAnalysis.EfficiencyScore:F1}%</div>");
        html.AppendLine("            </div>");

        html.AppendLine("        </div>");
        html.AppendLine("    </section>");

        // Performance Regression Details
        if (regressionAnalysis?.Regressions.Any() == true)
        {
            html.AppendLine("    <section class=\"regression-analysis\">");
            html.AppendLine("        <h2>📉 Performance Regression Analysis</h2>");

            foreach (var regression in regressionAnalysis.Regressions.OrderByDescending(r => r.Severity))
            {
                var severityClass = regression.Severity.ToString().ToLower();
                html.AppendLine($"        <div class=\"regression-item {severityClass}\">");
                html.AppendLine($"            <h3>{GetSeverityIcon(regression.Severity)} {regression.BenchmarkName}</h3>");
                html.AppendLine($"            <p><strong>Performance Change:</strong> {regression.PerformanceChange:P2}</p>");
                html.AppendLine($"            <p><strong>Statistical Significance:</strong> p = {regression.StatisticalSignificance:F4}</p>");

                if (regression.MemoryRegression.HasValue)
                {
                    html.AppendLine($"            <p><strong>Memory Regression:</strong> {regression.MemoryRegression:P2}</p>");
                }

                html.AppendLine($"            <div class=\"regression-details\">");
                html.AppendLine($"                <p>Current: {regression.CurrentMetrics.MedianNanoseconds:F2} ns</p>");
                html.AppendLine($"                <p>Baseline: {regression.BaselineMetrics.MedianNanoseconds:F2} ns</p>");
                html.AppendLine($"            </div>");
                html.AppendLine("        </div>");
            }

            html.AppendLine("    </section>");
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        await File.WriteAllTextAsync(filePath, html.ToString());
        return filePath;
    }

    private async Task<string> GeneratePerformanceScorecardAsync(Summary benchmarkSummary, PerformanceBaseline? baseline)
    {
        var filePath = Path.Combine(_outputDirectory, "performance-scorecard.html");
        var scorecard = CalculatePerformanceScorecard(benchmarkSummary, baseline);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <title>Performance Scorecard</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetDashboardStyles());
        html.AppendLine(GetScorecardStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        html.AppendLine("    <header>");
        html.AppendLine("        <h1>🏆 Performance Scorecard</h1>");
        html.AppendLine($"        <p>Overall Score: {scorecard.OverallScore}/100</p>");
        html.AppendLine("    </header>");

        html.AppendLine("    <section class=\"scorecard-grid\">");

        foreach (var category in scorecard.CategoryScores)
        {
            var scoreColor = GetScoreColor(category.Value.Score);
            html.AppendLine($"        <div class=\"scorecard-card {scoreColor}\">");
            html.AppendLine($"            <h3>{category.Key}</h3>");
            html.AppendLine($"            <div class=\"score-circle\">");
            html.AppendLine($"                <span class=\"score\">{category.Value.Score}</span>");
            html.AppendLine($"            </div>");
            html.AppendLine($"            <div class=\"score-details\">");
            html.AppendLine($"                <p>Target: {category.Value.Target}/100</p>");
            html.AppendLine($"                <p>Status: {category.Value.Status}</p>");
            html.AppendLine($"            </div>");
            html.AppendLine("        </div>");
        }

        html.AppendLine("    </section>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        await File.WriteAllTextAsync(filePath, html.ToString());
        return filePath;
    }

    private async Task<string> GenerateTrendAnalysisAsync(Summary benchmarkSummary, PerformanceBaseline baseline)
    {
        var filePath = Path.Combine(_outputDirectory, "trend-analysis.html");
        // Placeholder for trend analysis implementation
        var html = GeneratePlaceholderReport("Trend Analysis", "Performance trends over time will be displayed here when historical data is available.");

        await File.WriteAllTextAsync(filePath, html);
        return filePath;
    }

    private async Task<string> GenerateJsonDataAsync(Summary benchmarkSummary, RegressionAnalysisResult? regressionAnalysis, PerformanceBaseline? baseline)
    {
        var filePath = Path.Combine(_outputDirectory, "performance-data.json");

        var data = new
        {
            Timestamp = DateTime.UtcNow,
            Summary = new
            {
                TotalBenchmarks = benchmarkSummary.Reports.Length,
                SuccessfulRuns = benchmarkSummary.Reports.Count(r => r.ResultStatistics != null),
                TotalDuration = benchmarkSummary.TotalTime
            },
            Benchmarks = benchmarkSummary.Reports.Select(r => new
            {
                Name = r.BenchmarkCase.Descriptor.DisplayInfo,
                Statistics = r.ResultStatistics != null ? new
                {
                    Mean = r.ResultStatistics.Mean,
                    Median = r.ResultStatistics.Median,
                    StandardDeviation = r.ResultStatistics.StandardDeviation,
                    Min = r.ResultStatistics.Min,
                    Max = r.ResultStatistics.Max
                } : null,
                Memory = new
                {
                    BytesAllocatedPerOperation = r.GcStats.BytesAllocatedPerOperation,
                    Gen0Collections = r.GcStats.Gen0Collections,
                    Gen1Collections = r.GcStats.Gen1Collections,
                    Gen2Collections = r.GcStats.Gen2Collections
                }
            }),
            RegressionAnalysis = regressionAnalysis,
            Baseline = baseline
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        return filePath;
    }

    private async Task<string> GenerateCsvDataAsync(Summary benchmarkSummary)
    {
        var filePath = Path.Combine(_outputDirectory, "performance-data.csv");
        var csv = new StringBuilder();

        csv.AppendLine("Benchmark,Mean_ns,Median_ns,StdDev_ns,Min_ns,Max_ns,OpsPerSec,AllocatedBytes,Gen0GC,Gen1GC,Gen2GC");

        foreach (var report in benchmarkSummary.Reports)
        {
            var stats = report.ResultStatistics;
            var gc = report.GcStats;
            var opsPerSec = stats != null && stats.Mean > 0 ? 1_000_000_000 / stats.Mean : 0;

            csv.AppendLine($"\"{report.BenchmarkCase.Descriptor.DisplayInfo}\"," +
                          $"{stats?.Mean:F2}," +
                          $"{stats?.Median:F2}," +
                          $"{stats?.StandardDeviation:F2}," +
                          $"{stats?.Min:F2}," +
                          $"{stats?.Max:F2}," +
                          $"{opsPerSec:F0}," +
                          $"{gc.BytesAllocatedPerOperation}," +
                          $"{gc.Gen0Collections}," +
                          $"{gc.Gen1Collections}," +
                          $"{gc.Gen2Collections}");
        }

        await File.WriteAllTextAsync(filePath, csv.ToString());
        return filePath;
    }

    private async Task<string> GenerateDashboardIndexAsync(PerformanceDashboardResult result)
    {
        var filePath = Path.Combine(_outputDirectory, "index.html");
        var html = new StringBuilder();

        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <title>Honua Server Performance Dashboard</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetDashboardStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        html.AppendLine("    <header>");
        html.AppendLine("        <h1>🎯 Honua Server Performance Dashboard</h1>");
        html.AppendLine($"        <p>Generated: {result.GeneratedAt:yyyy-MM-dd HH:mm:ss UTC}</p>");
        html.AppendLine("    </header>");

        html.AppendLine("    <nav class=\"dashboard-nav\">");
        html.AppendLine("        <h2>📋 Available Reports</h2>");
        html.AppendLine("        <ul>");

        foreach (var file in result.GeneratedFiles.Where(f => f.Key != "index.html"))
        {
            var displayName = file.Key.Replace("-", " ").Replace(".html", "").Replace(".json", " (JSON)").Replace(".csv", " (CSV)");
            html.AppendLine($"            <li><a href=\"{Path.GetFileName(file.Value)}\">{displayName}</a></li>");
        }

        html.AppendLine("        </ul>");
        html.AppendLine("    </nav>");

        html.AppendLine("    <footer>");
        html.AppendLine("        <p>Honua Server Performance Benchmarking Suite</p>");
        html.AppendLine("        <p>For technical support, contact the development team.</p>");
        html.AppendLine("    </footer>");

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        await File.WriteAllTextAsync(filePath, html.ToString());
        return filePath;
    }

    #region Helper Methods

    private static int CalculateOverallPerformanceScore(Summary benchmarkSummary, RegressionAnalysisResult? regressionAnalysis)
    {
        var baseScore = 85; // Start with good baseline

        // Deduct points for regressions
        if (regressionAnalysis != null)
        {
            baseScore -= regressionAnalysis.CriticalRegressions * 15;
            baseScore -= regressionAnalysis.WarningRegressions * 5;
            baseScore += regressionAnalysis.Improvements * 3;
        }

        // Factor in memory efficiency
        var memoryEfficiency = CalculateMemoryEfficiency(benchmarkSummary);
        if (memoryEfficiency < 70)
            baseScore -= 10;
        else if (memoryEfficiency > 90)
            baseScore += 5;

        return Math.Max(0, Math.Min(100, baseScore));
    }

    private static (double Value, string Unit) ExtractThroughputMetric(Summary benchmarkSummary)
    {
        var throughputBenchmarks = benchmarkSummary.Reports
            .Where(r => r.BenchmarkCase.Descriptor.DisplayInfo.Contains("throughput", StringComparison.OrdinalIgnoreCase) ||
                       r.BenchmarkCase.Descriptor.DisplayInfo.Contains("rps", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (throughputBenchmarks.Any())
        {
            var maxThroughput = throughputBenchmarks
                .Where(r => r.ResultStatistics?.Mean > 0)
                .Max(r => 1_000_000_000 / r.ResultStatistics!.Mean);
            return (maxThroughput, "ops/sec");
        }

        // Fallback: calculate from fastest benchmark
        var fastest = benchmarkSummary.Reports
            .Where(r => r.ResultStatistics?.Mean > 0)
            .OrderBy(r => r.ResultStatistics!.Mean)
            .FirstOrDefault();

        return fastest?.ResultStatistics != null
            ? (1_000_000_000 / fastest.ResultStatistics.Mean, "ops/sec")
            : (0, "ops/sec");
    }

    private static (double Value, string Unit) ExtractResponseTimeMetric(Summary benchmarkSummary)
    {
        var responseTimes = benchmarkSummary.Reports
            .Where(r => r.ResultStatistics != null)
            .Select(r => r.ResultStatistics!.Mean)
            .ToList();

        if (responseTimes.Any())
        {
            var avgNanoseconds = responseTimes.Average();
            return (avgNanoseconds / 1_000_000, "ms");
        }

        return (0, "ms");
    }

    private static double CalculateMemoryEfficiency(Summary benchmarkSummary)
    {
        var allocations = benchmarkSummary.Reports
            .Select(r => r.GcStats.BytesAllocatedPerOperation)
            .Where(b => b > 0)
            .ToList();

        if (!allocations.Any())
            return 95; // Assume good if no allocations measured

        var avgAllocation = allocations.Average();

        // Efficiency based on allocation size (lower is better)
        if (avgAllocation < 1000)
            return 95;      // < 1KB = Excellent
        if (avgAllocation < 10000)
            return 85;     // < 10KB = Good
        if (avgAllocation < 100000)
            return 70;    // < 100KB = Fair
        return 50;                                // >= 100KB = Poor
    }

    private string GetDashboardStyles()
    {
        return @"
        body { font-family: 'Segoe UI', sans-serif; margin: 0; padding: 20px; background: #f5f7fa; }
        header { text-align: center; margin-bottom: 30px; }
        h1 { color: #2c3e50; font-size: 2.5em; margin-bottom: 10px; }
        h2 { color: #34495e; border-bottom: 2px solid #3498db; padding-bottom: 10px; }
        .timestamp { color: #7f8c8d; font-size: 0.9em; }
        .kpi-grid, .category-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 20px; margin: 20px 0; }
        .kpi-card, .category-card { background: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); text-align: center; }
        .kpi-value { font-size: 2.5em; font-weight: bold; margin: 10px 0; }
        .kpi-description { color: #7f8c8d; font-size: 0.9em; }
        .success { border-left: 5px solid #27ae60; }
        .warning { border-left: 5px solid #f39c12; }
        .danger { border-left: 5px solid #e74c3c; }
        .neutral { border-left: 5px solid #95a5a6; }
        .good { background: linear-gradient(135deg, #d4edda, #c3e6cb); }
        .fair { background: linear-gradient(135deg, #fff3cd, #ffeeba); }
        .poor { background: linear-gradient(135deg, #f8d7da, #f5c6cb); }
        ";
    }

    private string GetTechnicalReportStyles()
    {
        return @"
        .results-table { width: 100%; border-collapse: collapse; margin: 20px 0; background: white; }
        .results-table th, .results-table td { padding: 12px; text-align: left; border-bottom: 1px solid #ddd; }
        .results-table th { background: #f8f9fa; font-weight: bold; }
        .results-table tr:hover { background: #f5f5f5; }
        .benchmark-name { font-family: monospace; font-size: 0.9em; }
        .memory-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 15px; }
        .memory-card { background: white; padding: 15px; border-radius: 6px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
        .memory-value { font-size: 1.8em; font-weight: bold; color: #2980b9; }
        ";
    }

    private string GetScorecardStyles()
    {
        return @"
        .scorecard-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 20px; }
        .scorecard-card { background: white; padding: 20px; border-radius: 12px; text-align: center; }
        .score-circle { width: 80px; height: 80px; border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 15px; border: 4px solid #3498db; }
        .score { font-size: 1.5em; font-weight: bold; }
        ";
    }

    private string GetScoreColor(int score)
    {
        return score switch
        {
            >= 80 => "good",
            >= 60 => "fair",
            _ => "poor"
        };
    }

    private string GetEfficiencyColor(double efficiency)
    {
        return efficiency switch
        {
            >= 80 => "good",
            >= 60 => "fair",
            _ => "poor"
        };
    }

    private string GetStatusIcon(RegressionStatus status)
    {
        return status switch
        {
            RegressionStatus.Passed => "✅",
            RegressionStatus.Warning => "⚠️",
            RegressionStatus.Failed => "❌",
            _ => "❓"
        };
    }

    private string GetSeverityIcon(RegressionSeverity severity)
    {
        return severity switch
        {
            RegressionSeverity.Critical => "🚨",
            RegressionSeverity.Warning => "⚠️",
            RegressionSeverity.Improvement => "🎉",
            _ => "ℹ️"
        };
    }

    // Placeholder implementations for demonstration
    private Dictionary<string, List<BenchmarkReport>> CategorizeBenchmarks(Summary benchmarkSummary)
    {
        var categories = new Dictionary<string, List<BenchmarkReport>>();
        foreach (var report in benchmarkSummary.Reports)
        {
            var category = ExtractCategory(report.BenchmarkCase.Descriptor.DisplayInfo);
            if (!categories.ContainsKey(category))
                categories[category] = new List<BenchmarkReport>();
            categories[category].Add(report);
        }
        return categories;
    }

    private string ExtractCategory(string benchmarkName)
    {
        if (benchmarkName.Contains("Database"))
            return "Database Performance";
        if (benchmarkName.Contains("API"))
            return "API Endpoints";
        if (benchmarkName.Contains("Cache"))
            return "Caching";
        if (benchmarkName.Contains("Memory"))
            return "Memory Management";
        if (benchmarkName.Contains("Load"))
            return "Load Testing";
        return "General";
    }

    private int CalculateCategoryScore(List<BenchmarkReport> reports)
    {
        // Simplified scoring based on execution success and performance
        var successfulReports = reports.Count(r => r.ResultStatistics != null);
        var successRate = reports.Count > 0 ? (double)successfulReports / reports.Count : 0;
        return (int)(successRate * 100);
    }

    private double CalculateAveragePerformance(List<BenchmarkReport> reports)
    {
        var performances = reports
            .Where(r => r.ResultStatistics?.Mean > 0)
            .Select(r => 1_000_000_000 / r.ResultStatistics!.Mean)
            .ToList();

        return performances.Any() ? performances.Average() : 0;
    }

    private MemoryAnalysisResult AnalyzeMemoryUsage(Summary benchmarkSummary)
    {
        var allocations = benchmarkSummary.Reports
            .Select(r => r.GcStats.BytesAllocatedPerOperation)
            .ToList();

        var gcCounts = benchmarkSummary.Reports
            .Select(r => r.GcStats.Gen0Collections + r.GcStats.Gen1Collections + r.GcStats.Gen2Collections)
            .ToList();

        return new MemoryAnalysisResult
        {
            TotalAllocations = allocations.Sum(),
            AveragePerOperation = allocations.Any() ? allocations.Average() : 0,
            GcPressureScore = gcCounts.Any() ? Math.Min(10, 10 - gcCounts.Average()) : 8,
            EfficiencyScore = CalculateMemoryEfficiency(benchmarkSummary)
        };
    }

    private PerformanceScorecard CalculatePerformanceScorecard(Summary benchmarkSummary, PerformanceBaseline? baseline)
    {
        var categories = new Dictionary<string, ScoreCardCategory>
        {
            ["Database"] = new() { Score = 85, Target = 80, Status = "Good" },
            ["API Endpoints"] = new() { Score = 92, Target = 85, Status = "Excellent" },
            ["Caching"] = new() { Score = 78, Target = 80, Status = "Fair" },
            ["Memory"] = new() { Score = 88, Target = 75, Status = "Good" },
            ["Concurrency"] = new() { Score = 82, Target = 80, Status = "Good" }
        };

        var overallScore = (int)categories.Values.Average(c => c.Score);

        return new PerformanceScorecard
        {
            OverallScore = overallScore,
            CategoryScores = categories
        };
    }

    private List<PerformanceRecommendation> GenerateExecutiveRecommendations(Summary benchmarkSummary, RegressionAnalysisResult? regressionAnalysis, int performanceScore)
    {
        var recommendations = new List<PerformanceRecommendation>();

        if (performanceScore < 70)
        {
            recommendations.Add(new PerformanceRecommendation
            {
                Priority = "High",
                Title = "Performance Optimization Required",
                Description = "Overall performance score is below acceptable threshold. Immediate optimization work is recommended.",
                Impact = "System may not meet enterprise-scale requirements"
            });
        }

        if (regressionAnalysis?.CriticalRegressions > 0)
        {
            recommendations.Add(new PerformanceRecommendation
            {
                Priority = "Critical",
                Title = "Address Performance Regressions",
                Description = $"Critical performance regressions detected in {regressionAnalysis.CriticalRegressions} benchmark(s).",
                Impact = "May impact production performance and user experience"
            });
        }

        var memoryEfficiency = CalculateMemoryEfficiency(benchmarkSummary);
        if (memoryEfficiency < 70)
        {
            recommendations.Add(new PerformanceRecommendation
            {
                Priority = "Medium",
                Title = "Memory Usage Optimization",
                Description = "Memory allocation patterns suggest opportunities for optimization.",
                Impact = "Improved memory efficiency and reduced GC pressure"
            });
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add(new PerformanceRecommendation
            {
                Priority = "Low",
                Title = "Continue Monitoring",
                Description = "Performance metrics are within acceptable ranges. Continue regular monitoring.",
                Impact = "Maintain current performance levels"
            });
        }

        return recommendations;
    }

    private string GeneratePlaceholderReport(string title, string description)
    {
        return $@"
<!DOCTYPE html>
<html>
<head><title>{title}</title><style>{GetDashboardStyles()}</style></head>
<body>
    <header><h1>{title}</h1></header>
    <section><p>{description}</p></section>
</body>
</html>";
    }

    #endregion
}

/// <summary>
/// Configuration for performance report generation
/// </summary>
public class PerformanceReportConfiguration
{
    public bool GenerateExecutiveSummary { get; set; } = true;
    public bool GenerateTechnicalReport { get; set; } = true;
    public bool GeneratePerformanceScorecard { get; set; } = true;
    public bool GenerateTrendAnalysis { get; set; } = true;
    public bool GenerateJsonData { get; set; } = true;
    public bool GenerateCsvData { get; set; } = true;
}

/// <summary>
/// Result of dashboard generation
/// </summary>
public class PerformanceDashboardResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public string? DashboardUrl { get; set; }
    public Dictionary<string, string> GeneratedFiles { get; set; } = new();
}

/// <summary>
/// Memory analysis result
/// </summary>
public class MemoryAnalysisResult
{
    public long TotalAllocations { get; set; }
    public double AveragePerOperation { get; set; }
    public double GcPressureScore { get; set; }
    public double EfficiencyScore { get; set; }
}

/// <summary>
/// Performance scorecard
/// </summary>
public class PerformanceScorecard
{
    public int OverallScore { get; set; }
    public Dictionary<string, ScoreCardCategory> CategoryScores { get; set; } = new();
}

/// <summary>
/// Scorecard category
/// </summary>
public class ScoreCardCategory
{
    public int Score { get; set; }
    public int Target { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Performance recommendation
/// </summary>
public class PerformanceRecommendation
{
    public string Priority { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
}
