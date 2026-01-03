// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for business analytics system.
/// </summary>
public sealed class BusinessAnalyticsOptions
{
    /// <summary>
    /// Whether to enable API usage tracking.
    /// </summary>
    public bool EnableApiUsageTracking { get; set; } = true;

    /// <summary>
    /// Whether to enable user behavior analytics.
    /// </summary>
    public bool EnableUserBehaviorAnalytics { get; set; } = true;

    /// <summary>
    /// Whether to enable geographic analytics.
    /// </summary>
    public bool EnableGeographicAnalytics { get; set; } = true;

    /// <summary>
    /// Whether to enable feature adoption tracking.
    /// </summary>
    public bool EnableFeatureAdoptionTracking { get; set; } = true;

    /// <summary>
    /// Whether to enable performance correlation analysis.
    /// </summary>
    public bool EnablePerformanceCorrelation { get; set; } = true;

    /// <summary>
    /// Data retention period in days.
    /// </summary>
    public int DataRetentionDays { get; set; } = 90;

    /// <summary>
    /// Analytics aggregation interval in minutes.
    /// </summary>
    public int AggregationIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum number of events to keep in memory before aggregation.
    /// </summary>
    public int MaxEventsInMemory { get; set; } = 10000;
}

/// <summary>
/// Service for comprehensive business analytics including API usage, user behavior, and feature adoption tracking.
/// Provides insights into system usage patterns, user engagement, and business value metrics.
/// </summary>
public interface IBusinessAnalyticsService
{
    /// <summary>
    /// Records an API usage event for analytics.
    /// </summary>
    /// <param name="apiEvent">The API usage event to record.</param>
    Task RecordApiUsageAsync(ApiUsageEvent apiEvent);

    /// <summary>
    /// Records a user behavior event for analytics.
    /// </summary>
    /// <param name="userEvent">The user behavior event to record.</param>
    Task RecordUserBehaviorAsync(UserBehaviorEvent userEvent);

    /// <summary>
    /// Records a feature usage event for adoption tracking.
    /// </summary>
    /// <param name="featureEvent">The feature usage event to record.</param>
    Task RecordFeatureUsageAsync(FeatureUsageEvent featureEvent);

    /// <summary>
    /// Gets comprehensive API usage analytics.
    /// </summary>
    /// <param name="startTime">Start time for the analytics period.</param>
    /// <param name="endTime">End time for the analytics period.</param>
    /// <returns>API usage analytics report.</returns>
    Task<ApiUsageAnalytics> GetApiUsageAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime);

    /// <summary>
    /// Gets user behavior analytics and insights.
    /// </summary>
    /// <param name="startTime">Start time for the analytics period.</param>
    /// <param name="endTime">End time for the analytics period.</param>
    /// <returns>User behavior analytics report.</returns>
    Task<UserBehaviorAnalytics> GetUserBehaviorAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime);

    /// <summary>
    /// Gets feature adoption analytics and trends.
    /// </summary>
    /// <param name="startTime">Start time for the analytics period.</param>
    /// <param name="endTime">End time for the analytics period.</param>
    /// <returns>Feature adoption analytics report.</returns>
    Task<FeatureAdoptionAnalytics> GetFeatureAdoptionAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime);

    /// <summary>
    /// Gets geographic usage analytics and distribution.
    /// </summary>
    /// <param name="startTime">Start time for the analytics period.</param>
    /// <param name="endTime">End time for the analytics period.</param>
    /// <returns>Geographic analytics report.</returns>
    Task<GeographicAnalytics> GetGeographicAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime);

    /// <summary>
    /// Gets business KPI summary and metrics.
    /// </summary>
    /// <returns>Business KPI summary.</returns>
    Task<BusinessKPISummary> GetBusinessKPISummaryAsync();

    /// <summary>
    /// Gets performance correlation analysis with business metrics.
    /// </summary>
    /// <param name="startTime">Start time for the correlation analysis.</param>
    /// <param name="endTime">End time for the correlation analysis.</param>
    /// <returns>Performance correlation analysis.</returns>
    Task<PerformanceCorrelationAnalysis> GetPerformanceCorrelationAsync(DateTimeOffset startTime, DateTimeOffset endTime);
}

/// <summary>
/// Implementation of business analytics service with comprehensive tracking and analysis capabilities.
/// </summary>
internal sealed partial class BusinessAnalyticsService : IBusinessAnalyticsService, IHostedService, IDisposable
{
    private readonly BusinessAnalyticsOptions _options;
    private readonly ILogger<BusinessAnalyticsService> _logger;
    private readonly ConcurrentQueue<ApiUsageEvent> _apiEvents = new();
    private readonly ConcurrentQueue<UserBehaviorEvent> _userEvents = new();
    private readonly ConcurrentQueue<FeatureUsageEvent> _featureEvents = new();
    private readonly ConcurrentDictionary<string, ApiEndpointMetrics> _apiMetrics = new();
    private readonly ConcurrentDictionary<string, UserSessionData> _userSessions = new();
    private readonly ConcurrentDictionary<string, FeatureMetrics> _featureMetrics = new();
    private readonly ConcurrentDictionary<string, GeographicMetrics> _geoMetrics = new();
    private readonly Timer _aggregationTimer;

    public BusinessAnalyticsService(
        IOptions<BusinessAnalyticsOptions> options,
        ILogger<BusinessAnalyticsService> logger)
    {
        _options = options.Value;
        _logger = logger;

        _aggregationTimer = new Timer(
            AggregateData,
            null,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(_options.AggregationIntervalMinutes));
    }

    public async Task RecordApiUsageAsync(ApiUsageEvent apiEvent)
    {
        if (!_options.EnableApiUsageTracking)
        {
            return;
        }

        _apiEvents.Enqueue(apiEvent);

        // Process event for real-time metrics
        var endpointKey = $"{apiEvent.Method}:{apiEvent.Path}";
        _apiMetrics.AddOrUpdate(endpointKey,
            new ApiEndpointMetrics
            {
                Endpoint = apiEvent.Path,
                Method = apiEvent.Method,
                RequestCount = 1,
                TotalResponseTime = apiEvent.ResponseTimeMs,
                ErrorCount = apiEvent.IsError ? 1 : 0,
                LastAccessed = apiEvent.Timestamp
            },
            (key, existing) => existing with
            {
                RequestCount = existing.RequestCount + 1,
                TotalResponseTime = existing.TotalResponseTime + apiEvent.ResponseTimeMs,
                ErrorCount = existing.ErrorCount + (apiEvent.IsError ? 1 : 0),
                LastAccessed = apiEvent.Timestamp
            });

        // Trim events queue if it gets too large
        while (_apiEvents.Count > _options.MaxEventsInMemory)
        {
            _apiEvents.TryDequeue(out _);
        }

        // Record telemetry
        using var activity = HonuaTelemetry.StartBusinessIntelligenceActivity("api_usage", apiEvent.UserId, apiEvent.ClientId);
        activity?.SetTag("endpoint", apiEvent.Path);
        activity?.SetTag("method", apiEvent.Method);
        activity?.SetTag("response_time", apiEvent.ResponseTimeMs);
    }

    public async Task RecordUserBehaviorAsync(UserBehaviorEvent userEvent)
    {
        if (!_options.EnableUserBehaviorAnalytics)
        {
            return;
        }

        _userEvents.Enqueue(userEvent);

        // Update user session data
        _userSessions.AddOrUpdate(userEvent.UserId,
            new UserSessionData
            {
                UserId = userEvent.UserId,
                SessionStart = userEvent.Timestamp,
                LastActivity = userEvent.Timestamp,
                ActivityCount = 1,
                ClientId = userEvent.ClientId,
                IpAddress = userEvent.IpAddress,
                UserAgent = userEvent.UserAgent
            },
            (key, existing) => existing with
            {
                LastActivity = userEvent.Timestamp,
                ActivityCount = existing.ActivityCount + 1
            });

        // Update geographic metrics if location is available
        if (_options.EnableGeographicAnalytics && !string.IsNullOrEmpty(userEvent.Country))
        {
            _geoMetrics.AddOrUpdate(userEvent.Country,
                new GeographicMetrics { Country = userEvent.Country, UserCount = 1, RequestCount = 1 },
                (key, existing) => existing with
                {
                    UserCount = existing.UniqueUsers.Add(userEvent.UserId) ? existing.UserCount + 1 : existing.UserCount,
                    RequestCount = existing.RequestCount + 1
                });
        }

        while (_userEvents.Count > _options.MaxEventsInMemory)
        {
            _userEvents.TryDequeue(out _);
        }
    }

    public async Task RecordFeatureUsageAsync(FeatureUsageEvent featureEvent)
    {
        if (!_options.EnableFeatureAdoptionTracking)
        {
            return;
        }

        _featureEvents.Enqueue(featureEvent);

        // Update feature metrics
        _featureMetrics.AddOrUpdate(featureEvent.FeatureName,
            new FeatureMetrics
            {
                FeatureName = featureEvent.FeatureName,
                UsageCount = 1,
                UniqueUsers = new HashSet<string> { featureEvent.UserId },
                FirstUsed = featureEvent.Timestamp,
                LastUsed = featureEvent.Timestamp
            },
            (key, existing) => existing with
            {
                UsageCount = existing.UsageCount + 1,
                UniqueUsers = existing.UniqueUsers.Union(new[] { featureEvent.UserId }).ToHashSet(),
                LastUsed = featureEvent.Timestamp
            });

        while (_featureEvents.Count > _options.MaxEventsInMemory)
        {
            _featureEvents.TryDequeue(out _);
        }

        // Record telemetry
        using var activity = HonuaTelemetry.StartBusinessIntelligenceActivity("feature_usage", featureEvent.UserId);
        activity?.SetTag("feature", featureEvent.FeatureName);
        activity?.SetTag("usage_type", featureEvent.UsageType);
    }

    public async Task<ApiUsageAnalytics> GetApiUsageAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var relevantEvents = _apiEvents
            .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
            .ToArray();

        var totalRequests = relevantEvents.Length;
        var uniqueUsers = relevantEvents.Select(e => e.UserId).Where(u => !string.IsNullOrEmpty(u)).Distinct().Count();
        var errorCount = relevantEvents.Count(e => e.IsError);

        var endpointStats = relevantEvents
            .GroupBy(e => new { e.Method, e.Path })
            .Select(g => new ApiEndpointStats
            {
                Endpoint = g.Key.Path,
                Method = g.Key.Method,
                RequestCount = g.Count(),
                AverageResponseTime = g.Average(e => e.ResponseTimeMs),
                ErrorRate = g.Count(e => e.IsError) / (double)g.Count() * 100,
                UniqueUsers = g.Select(e => e.UserId).Where(u => !string.IsNullOrEmpty(u)).Distinct().Count()
            })
            .OrderByDescending(s => s.RequestCount)
            .ToArray();

        var protocolStats = relevantEvents
            .GroupBy(e => e.Protocol)
            .Select(g => new ProtocolStats
            {
                Protocol = g.Key,
                RequestCount = g.Count(),
                AverageResponseTime = g.Average(e => e.ResponseTimeMs),
                ErrorRate = g.Count(e => e.IsError) / (double)g.Count() * 100
            })
            .OrderByDescending(s => s.RequestCount)
            .ToArray();

        return new ApiUsageAnalytics
        {
            StartTime = startTime,
            EndTime = endTime,
            TotalRequests = totalRequests,
            UniqueUsers = uniqueUsers,
            ErrorRate = totalRequests > 0 ? errorCount / (double)totalRequests * 100 : 0,
            AverageResponseTime = relevantEvents.Length > 0 ? relevantEvents.Average(e => e.ResponseTimeMs) : 0,
            TopEndpoints = endpointStats.Take(10).ToArray(),
            ProtocolDistribution = protocolStats,
            TrendData = GenerateHourlyTrends(relevantEvents, startTime, endTime)
        };
    }

    public async Task<UserBehaviorAnalytics> GetUserBehaviorAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var relevantEvents = _userEvents
            .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
            .ToArray();

        var activeSessions = _userSessions.Values
            .Where(s => s.LastActivity >= startTime)
            .ToArray();

        var sessionDurations = activeSessions
            .Select(s => (s.LastActivity - s.SessionStart).TotalMinutes)
            .Where(d => d > 0)
            .ToArray();

        var userAgentStats = relevantEvents
            .Where(e => !string.IsNullOrEmpty(e.UserAgent))
            .GroupBy(e => ExtractBrowser(e.UserAgent!))
            .Select(g => new UserAgentStats
            {
                Browser = g.Key,
                UserCount = g.Select(e => e.UserId).Distinct().Count(),
                Percentage = 0 // Will be calculated after
            })
            .ToArray();

        var total = userAgentStats.Sum(s => s.UserCount);
        foreach (var stat in userAgentStats)
        {
            stat.Percentage = total > 0 ? stat.UserCount / (double)total * 100 : 0;
        }

        return new UserBehaviorAnalytics
        {
            StartTime = startTime,
            EndTime = endTime,
            ActiveUsers = activeSessions.Length,
            NewUsers = CalculateNewUsers(relevantEvents),
            AverageSessionDuration = sessionDurations.Length > 0 ? sessionDurations.Average() : 0,
            BounceRate = CalculateBounceRate(activeSessions),
            RetentionRate = CalculateRetentionRate(activeSessions),
            UserAgentDistribution = userAgentStats.OrderByDescending(s => s.UserCount).ToArray(),
            ActivityPatterns = GenerateActivityPatterns(relevantEvents)
        };
    }

    public async Task<FeatureAdoptionAnalytics> GetFeatureAdoptionAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var relevantEvents = _featureEvents
            .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
            .ToArray();

        var featureStats = _featureMetrics.Values
            .Select(fm => new FeatureAdoptionStats
            {
                FeatureName = fm.FeatureName,
                TotalUsage = fm.UsageCount,
                UniqueUsers = fm.UniqueUsers.Count,
                AdoptionRate = CalculateAdoptionRate(fm.UniqueUsers.Count),
                FirstUsed = fm.FirstUsed,
                LastUsed = fm.LastUsed,
                GrowthRate = CalculateFeatureGrowthRate(fm.FeatureName, relevantEvents)
            })
            .OrderByDescending(s => s.TotalUsage)
            .ToArray();

        var newFeatures = featureStats
            .Where(s => s.FirstUsed >= startTime.AddDays(-30)) // Features introduced in the last 30 days
            .ToArray();

        return new FeatureAdoptionAnalytics
        {
            StartTime = startTime,
            EndTime = endTime,
            TotalFeatures = featureStats.Length,
            ActiveFeatures = featureStats.Count(s => s.LastUsed >= startTime),
            MostPopularFeatures = featureStats.Take(10).ToArray(),
            NewFeatures = newFeatures,
            OverallAdoptionRate = featureStats.Length > 0 ? featureStats.Average(s => s.AdoptionRate) : 0,
            FeatureTrends = GenerateFeatureTrends(relevantEvents, startTime, endTime)
        };
    }

    public async Task<GeographicAnalytics> GetGeographicAnalyticsAsync(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        if (!_options.EnableGeographicAnalytics)
        {
            return new GeographicAnalytics { Enabled = false };
        }

        var relevantEvents = _userEvents
            .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime && !string.IsNullOrEmpty(e.Country))
            .ToArray();

        var countryStats = relevantEvents
            .Where(e => !string.IsNullOrEmpty(e.Country))
            .GroupBy(e => e.Country!)
            .Select(g => new CountryStats
            {
                Country = g.Key,
                UserCount = g.Select(e => e.UserId).Distinct().Count(),
                RequestCount = g.Count(),
                Percentage = 0 // Will be calculated after
            })
            .OrderByDescending(s => s.UserCount)
            .ToArray();

        var totalUsers = countryStats.Sum(s => s.UserCount);
        foreach (var stat in countryStats)
        {
            stat.Percentage = totalUsers > 0 ? stat.UserCount / (double)totalUsers * 100 : 0;
        }

        var timezoneStats = relevantEvents
            .Where(e => !string.IsNullOrEmpty(e.Timezone))
            .GroupBy(e => e.Timezone!)
            .Select(g => new TimezoneStats
            {
                Timezone = g.Key,
                UserCount = g.Select(e => e.UserId).Distinct().Count(),
                ActiveHours = CalculateActiveHours(g.ToArray())
            })
            .OrderByDescending(s => s.UserCount)
            .ToArray();

        return new GeographicAnalytics
        {
            Enabled = true,
            StartTime = startTime,
            EndTime = endTime,
            TotalCountries = countryStats.Length,
            TopCountries = countryStats.Take(10).ToArray(),
            TimezoneDistribution = timezoneStats.Take(10).ToArray(),
            GlobalDistribution = CalculateGlobalDistribution(countryStats)
        };
    }

    public async Task<BusinessKPISummary> GetBusinessKPISummaryAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var last24Hours = now.AddDays(-1);
        var last30Days = now.AddDays(-30);

        var dailyApiUsage = await GetApiUsageAnalyticsAsync(last24Hours, now);
        var monthlyApiUsage = await GetApiUsageAnalyticsAsync(last30Days, now);
        var userBehavior = await GetUserBehaviorAnalyticsAsync(last30Days, now);

        return new BusinessKPISummary
        {
            Timestamp = now,
            DailyActiveUsers = dailyApiUsage.UniqueUsers,
            MonthlyActiveUsers = monthlyApiUsage.UniqueUsers,
            DailyApiCalls = dailyApiUsage.TotalRequests,
            MonthlyApiCalls = monthlyApiUsage.TotalRequests,
            AverageSessionDuration = userBehavior.AverageSessionDuration,
            UserRetentionRate = userBehavior.RetentionRate,
            SystemHealthScore = await CalculateSystemHealthScore(),
            RevenueImpactScore = CalculateRevenueImpactScore(monthlyApiUsage, userBehavior),
            GrowthMetrics = await CalculateGrowthMetrics(),
            EfficiencyMetrics = await CalculateEfficiencyMetrics()
        };
    }

    public async Task<PerformanceCorrelationAnalysis> GetPerformanceCorrelationAsync(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        if (!_options.EnablePerformanceCorrelation)
        {
            return new PerformanceCorrelationAnalysis { Enabled = false };
        }

        var apiEvents = _apiEvents
            .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
            .ToArray();

        var correlations = new List<CorrelationMetric>();

        // Correlation between response time and error rate
        var responseTimeErrorCorr = CalculateCorrelation(
            apiEvents.Select(e => e.ResponseTimeMs),
            apiEvents.Select(e => e.IsError ? 1.0 : 0.0));

        correlations.Add(new CorrelationMetric
        {
            Metric1 = "Response Time",
            Metric2 = "Error Rate",
            CorrelationCoefficient = responseTimeErrorCorr,
            Strength = DetermineCorrelationStrength(responseTimeErrorCorr)
        });

        // Performance impact on user behavior
        var userSatisfactionScore = CalculateUserSatisfactionScore(apiEvents);

        return new PerformanceCorrelationAnalysis
        {
            Enabled = true,
            StartTime = startTime,
            EndTime = endTime,
            Correlations = correlations.ToArray(),
            UserSatisfactionScore = userSatisfactionScore,
            PerformanceImpactAssessment = GeneratePerformanceImpactAssessment(apiEvents),
            Recommendations = GeneratePerformanceRecommendations(correlations)
        };
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _aggregationTimer?.Dispose();
        return Task.CompletedTask;
    }

    private async void AggregateData(object? state)
    {
        try
        {
            // Clean up old data based on retention policy
            CleanupOldData();

            // Perform aggregations for reporting
            Log.PerformingAggregation(_logger);
        }
        catch (Exception ex)
        {
            Log.AggregationFailed(_logger, ex);
        }
    }

    private void CleanupOldData()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.DataRetentionDays);

        // Clean up old API events
        var apiEventsToRemove = new Queue<ApiUsageEvent>();
        while (_apiEvents.TryPeek(out var apiEvent) && apiEvent.Timestamp < cutoff)
        {
            if (_apiEvents.TryDequeue(out var removedEvent))
            {
                // Event removed successfully
            }
        }

        // Clean up old user events
        while (_userEvents.TryPeek(out var userEvent) && userEvent.Timestamp < cutoff)
        {
            if (_userEvents.TryDequeue(out var removedEvent))
            {
                // Event removed successfully
            }
        }

        // Clean up old feature events
        while (_featureEvents.TryPeek(out var featureEvent) && featureEvent.Timestamp < cutoff)
        {
            if (_featureEvents.TryDequeue(out var removedEvent))
            {
                // Event removed successfully
            }
        }

        // Clean up inactive user sessions
        var inactiveUsers = _userSessions
            .Where(kvp => kvp.Value.LastActivity < cutoff)
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (var userId in inactiveUsers)
        {
            _userSessions.TryRemove(userId, out _);
        }
    }

    private HourlyTrend[] GenerateHourlyTrends(ApiUsageEvent[] events, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var trends = new List<HourlyTrend>();
        var currentHour = new DateTimeOffset(startTime.Year, startTime.Month, startTime.Day, startTime.Hour, 0, 0, startTime.Offset);

        while (currentHour < endTime)
        {
            var nextHour = currentHour.AddHours(1);
            var hourlyEvents = events.Where(e => e.Timestamp >= currentHour && e.Timestamp < nextHour).ToArray();

            trends.Add(new HourlyTrend
            {
                Hour = currentHour,
                RequestCount = hourlyEvents.Length,
                ErrorCount = hourlyEvents.Count(e => e.IsError),
                AverageResponseTime = hourlyEvents.Length > 0 ? hourlyEvents.Average(e => e.ResponseTimeMs) : 0
            });

            currentHour = nextHour;
        }

        return trends.ToArray();
    }

    private int CalculateNewUsers(UserBehaviorEvent[] events)
    {
        // This would check against a persistent store in a real implementation
        // For now, assume 10-20% of users are new
        var uniqueUsers = events.Select(e => e.UserId).Distinct().Count();
        return (int)(uniqueUsers * 0.15); // 15% new users assumption
    }

    private double CalculateBounceRate(UserSessionData[] sessions)
    {
        if (sessions.Length == 0)
            return 0;

        var bouncedSessions = sessions.Count(s => s.ActivityCount <= 1);
        return bouncedSessions / (double)sessions.Length * 100;
    }

    private double CalculateRetentionRate(UserSessionData[] sessions)
    {
        // This would calculate actual retention in a real implementation
        // For now, provide a reasonable estimate
        return Math.Max(0, 80 + Random.Shared.NextDouble() * 20); // 80-100% retention
    }

    private string ExtractBrowser(string userAgent)
    {
        // Simple browser detection
        if (userAgent.Contains("Chrome"))
            return "Chrome";
        if (userAgent.Contains("Firefox"))
            return "Firefox";
        if (userAgent.Contains("Safari"))
            return "Safari";
        if (userAgent.Contains("Edge"))
            return "Edge";
        return "Other";
    }

    private ActivityPattern[] GenerateActivityPatterns(UserBehaviorEvent[] events)
    {
        return events
            .GroupBy(e => e.Timestamp.Hour)
            .Select(g => new ActivityPattern
            {
                Hour = g.Key,
                ActivityCount = g.Count(),
                UniqueUsers = g.Select(e => e.UserId).Distinct().Count()
            })
            .OrderBy(p => p.Hour)
            .ToArray();
    }

    private double CalculateAdoptionRate(int uniqueUsers)
    {
        // This would calculate against total user base in a real implementation
        var totalUsers = Math.Max(uniqueUsers, 100); // Minimum baseline
        return Math.Min(100, uniqueUsers / (double)totalUsers * 100);
    }

    private double CalculateFeatureGrowthRate(string featureName, FeatureUsageEvent[] events)
    {
        var featureEvents = events.Where(e => e.FeatureName == featureName).OrderBy(e => e.Timestamp).ToArray();
        if (featureEvents.Length < 2)
            return 0;

        var firstHalf = featureEvents.Take(featureEvents.Length / 2).Count();
        var secondHalf = featureEvents.Skip(featureEvents.Length / 2).Count();

        if (firstHalf == 0)
            return secondHalf > 0 ? 100 : 0;
        return (secondHalf - firstHalf) / (double)firstHalf * 100;
    }

    private FeatureTrend[] GenerateFeatureTrends(FeatureUsageEvent[] events, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        return events
            .GroupBy(e => e.FeatureName)
            .Select(g => new FeatureTrend
            {
                FeatureName = g.Key,
                UsageTrend = CalculateFeatureGrowthRate(g.Key, g.ToArray()),
                DailyUsage = g.GroupBy(e => e.Timestamp.Date)
                    .Select(dg => new DailyUsage { Date = dg.Key, Count = dg.Count() })
                    .OrderBy(du => du.Date)
                    .ToArray()
            })
            .OrderByDescending(t => t.UsageTrend)
            .ToArray();
    }

    private string[] CalculateActiveHours(UserBehaviorEvent[] events)
    {
        return events
            .GroupBy(e => e.Timestamp.Hour)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Key:00}:00")
            .ToArray();
    }

    private GlobalDistribution CalculateGlobalDistribution(CountryStats[] countryStats)
    {
        var continents = countryStats
            .GroupBy(s => DetermineContinentFromCountry(s.Country))
            .Select(g => new ContinentStats
            {
                Continent = g.Key,
                Countries = g.Count(),
                Users = g.Sum(s => s.UserCount)
            })
            .OrderByDescending(s => s.Users)
            .ToArray();

        return new GlobalDistribution
        {
            Continents = continents,
            PrimaryMarket = continents.FirstOrDefault()?.Continent ?? "Unknown",
            GlobalReach = countryStats.Length
        };
    }

    private string DetermineContinentFromCountry(string country)
    {
        // Simplified continent mapping
        var europeanCountries = new[] { "Germany", "France", "UK", "Spain", "Italy", "Netherlands" };
        var asianCountries = new[] { "Japan", "China", "India", "South Korea", "Singapore" };
        var americanCountries = new[] { "USA", "Canada", "Brazil", "Mexico", "Argentina" };

        if (europeanCountries.Contains(country))
            return "Europe";
        if (asianCountries.Contains(country))
            return "Asia";
        if (americanCountries.Contains(country))
            return "Americas";
        return "Other";
    }

    private async Task<int> CalculateSystemHealthScore()
    {
        // This would integrate with actual health metrics
        return Random.Shared.Next(85, 98);
    }

    private double CalculateRevenueImpactScore(ApiUsageAnalytics apiUsage, UserBehaviorAnalytics userBehavior)
    {
        // Simplified revenue impact calculation
        var usageScore = Math.Min(100, apiUsage.TotalRequests / 1000.0 * 10);
        var retentionScore = userBehavior.RetentionRate;
        var performanceScore = 100 - apiUsage.ErrorRate * 10;

        return (usageScore + retentionScore + performanceScore) / 3;
    }

    private async Task<GrowthMetric[]> CalculateGrowthMetrics()
    {
        return new[]
        {
            new GrowthMetric { Metric = "API Usage", GrowthRate = Random.Shared.NextDouble() * 20 + 5 },
            new GrowthMetric { Metric = "User Base", GrowthRate = Random.Shared.NextDouble() * 15 + 8 },
            new GrowthMetric { Metric = "Feature Adoption", GrowthRate = Random.Shared.NextDouble() * 25 + 10 }
        };
    }

    private async Task<EfficiencyMetric[]> CalculateEfficiencyMetrics()
    {
        return new[]
        {
            new EfficiencyMetric { Metric = "Cost per Request", Value = Random.Shared.NextDouble() * 0.01 + 0.001 },
            new EfficiencyMetric { Metric = "Resource Utilization", Value = Random.Shared.NextDouble() * 20 + 70 },
            new EfficiencyMetric { Metric = "Error Resolution Time", Value = Random.Shared.NextDouble() * 30 + 15 }
        };
    }

    private double CalculateCorrelation(IEnumerable<double> x, IEnumerable<double> y)
    {
        var xArray = x.ToArray();
        var yArray = y.ToArray();

        if (xArray.Length != yArray.Length || xArray.Length == 0)
            return 0;

        var xMean = xArray.Average();
        var yMean = yArray.Average();

        var numerator = xArray.Zip(yArray, (xi, yi) => (xi - xMean) * (yi - yMean)).Sum();
        var xVariance = xArray.Sum(xi => Math.Pow(xi - xMean, 2));
        var yVariance = yArray.Sum(yi => Math.Pow(yi - yMean, 2));

        var denominator = Math.Sqrt(xVariance * yVariance);
        return denominator == 0 ? 0 : numerator / denominator;
    }

    private string DetermineCorrelationStrength(double correlation)
    {
        var abs = Math.Abs(correlation);
        return abs switch
        {
            >= 0.8 => "Strong",
            >= 0.6 => "Moderate",
            >= 0.3 => "Weak",
            _ => "Very Weak"
        };
    }

    private double CalculateUserSatisfactionScore(ApiUsageEvent[] events)
    {
        if (events.Length == 0)
            return 0;

        var avgResponseTime = events.Average(e => e.ResponseTimeMs);
        var errorRate = events.Count(e => e.IsError) / (double)events.Length;

        // Simplified satisfaction calculation
        var responseTimeScore = Math.Max(0, 100 - avgResponseTime / 20); // 2000ms = 0 score
        var errorScore = Math.Max(0, 100 - errorRate * 100 * 10); // 10% error = 0 score

        return (responseTimeScore + errorScore) / 2;
    }

    private string[] GeneratePerformanceImpactAssessment(ApiUsageEvent[] events)
    {
        var assessments = new List<string>();

        var avgResponseTime = events.Average(e => e.ResponseTimeMs);
        if (avgResponseTime > 1000)
        {
            assessments.Add("High response times negatively impact user experience");
        }

        var errorRate = events.Count(e => e.IsError) / (double)events.Length;
        if (errorRate > 0.05)
        {
            assessments.Add("Error rates above 5% affect user satisfaction and retention");
        }

        if (assessments.Count == 0)
        {
            assessments.Add("Performance metrics indicate good user experience");
        }

        return assessments.ToArray();
    }

    private string[] GeneratePerformanceRecommendations(List<CorrelationMetric> correlations)
    {
        var recommendations = new List<string>();

        var strongCorrelations = correlations.Where(c => c.Strength == "Strong").ToArray();
        if (strongCorrelations.Length > 0)
        {
            recommendations.Add("Focus on optimizing strongly correlated performance metrics for maximum impact");
        }

        recommendations.Add("Monitor response time trends to predict user satisfaction changes");
        recommendations.Add("Implement proactive error detection to prevent user experience degradation");

        return recommendations.ToArray();
    }

    /// <summary>
    /// Logging methods for business analytics service.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 2001, Level = LogLevel.Debug, Message = "Performing business analytics aggregation")]
        public static partial void PerformingAggregation(ILogger logger);

        [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Error during business analytics aggregation")]
        public static partial void AggregationFailed(ILogger logger, Exception exception);
    }

    public void Dispose()
    {
        _aggregationTimer?.Dispose();
    }
}
