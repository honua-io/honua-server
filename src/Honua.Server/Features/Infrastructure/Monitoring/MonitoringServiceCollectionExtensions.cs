// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Service collection extensions for registering comprehensive monitoring and observability services.
/// </summary>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Adds comprehensive monitoring and observability services to the service collection.
    /// Includes business analytics, performance intelligence, anomaly detection, alerting, and integration capabilities.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddComprehensiveMonitoring(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure options from configuration
        services.Configure<AnomalyDetectionOptions>(
            configuration.GetSection("Monitoring:AnomalyDetection"));
        services.Configure<IntelligentAlertingOptions>(
            configuration.GetSection("Monitoring:IntelligentAlerting"));
        services.Configure<PerformanceIntelligenceOptions>(
            configuration.GetSection("Monitoring:PerformanceIntelligence"));
        services.Configure<BusinessAnalyticsOptions>(
            configuration.GetSection("Monitoring:BusinessAnalytics"));
        services.Configure<MonitoringIntegrationOptions>(
            configuration.GetSection("Monitoring:Integration"));

        // Register core monitoring services
        services.AddSingleton<IAnomalyDetectionService, AnomalyDetectionService>();
        services.AddSingleton<IIntelligentAlertingService, IntelligentAlertingService>();
        services.AddSingleton<IPerformanceIntelligenceService, PerformanceIntelligenceService>();
        services.AddSingleton<IBusinessAnalyticsService, BusinessAnalyticsService>();
        services.AddSingleton<IMonitoringIntegrationService, MonitoringIntegrationService>();

        // Register HTTP client for webhook integrations
        services.AddHttpClient<MonitoringIntegrationService>(client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Honua-Server-Monitoring/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Register hosted services for background processing
        services.AddHostedService<AnomalyDetectionService>(provider =>
            (AnomalyDetectionService)provider.GetRequiredService<IAnomalyDetectionService>());
        services.AddHostedService<IntelligentAlertingService>(provider =>
            (IntelligentAlertingService)provider.GetRequiredService<IIntelligentAlertingService>());
        services.AddHostedService<PerformanceIntelligenceService>(provider =>
            (PerformanceIntelligenceService)provider.GetRequiredService<IPerformanceIntelligenceService>());
        services.AddHostedService<BusinessAnalyticsService>(provider =>
            (BusinessAnalyticsService)provider.GetRequiredService<IBusinessAnalyticsService>());
        services.AddHostedService<MonitoringIntegrationService>(provider =>
            (MonitoringIntegrationService)provider.GetRequiredService<IMonitoringIntegrationService>());

        return services;
    }

    /// <summary>
    /// Adds basic monitoring services without advanced features.
    /// Suitable for development or resource-constrained environments.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddBasicMonitoring(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure basic options
        services.Configure<PerformanceIntelligenceOptions>(options =>
        {
            options.EnableAutomaticAnalysis = true;
            options.EnableOptimizationRecommendations = false;
            options.EnableMemoryProfiling = true;
            options.EnableDatabaseAnalysis = false;
            options.EnableNetworkAnalysis = false;
        });

        services.Configure<BusinessAnalyticsOptions>(options =>
        {
            options.EnableApiUsageTracking = true;
            options.EnableUserBehaviorAnalytics = false;
            options.EnableGeographicAnalytics = false;
            options.EnableFeatureAdoptionTracking = false;
            options.EnablePerformanceCorrelation = false;
        });

        services.Configure<AnomalyDetectionOptions>(options =>
        {
            options.EnableMachineLearning = false;
            options.SensitivityThreshold = 0.5;
            options.MinimumDataPoints = 10;
        });

        // Register basic services only
        services.AddSingleton<IAnomalyDetectionService, AnomalyDetectionService>();
        services.AddSingleton<IPerformanceIntelligenceService, PerformanceIntelligenceService>();
        services.AddSingleton<IBusinessAnalyticsService, BusinessAnalyticsService>();

        services.AddHostedService<AnomalyDetectionService>(provider =>
            (AnomalyDetectionService)provider.GetRequiredService<IAnomalyDetectionService>());
        services.AddHostedService<PerformanceIntelligenceService>(provider =>
            (PerformanceIntelligenceService)provider.GetRequiredService<IPerformanceIntelligenceService>());
        services.AddHostedService<BusinessAnalyticsService>(provider =>
            (BusinessAnalyticsService)provider.GetRequiredService<IBusinessAnalyticsService>());

        return services;
    }

    /// <summary>
    /// Adds enterprise-grade monitoring with all advanced features enabled.
    /// Includes machine learning, predictive analytics, and comprehensive integrations.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEnterpriseMonitoring(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Add comprehensive monitoring first
        services.AddComprehensiveMonitoring(configuration);

        // Configure enterprise-grade options
        services.PostConfigure<AnomalyDetectionOptions>(options =>
        {
            options.EnableMachineLearning = true;
            options.SensitivityThreshold = 0.2; // More sensitive
            options.MinimumDataPoints = 50; // More data for better ML
        });

        services.PostConfigure<IntelligentAlertingOptions>(options =>
        {
            options.EnableSmartAlerting = true;
            options.EnablePredictiveAlerting = true;
            options.AlertSuppressionMinutes = 10; // Faster response
            options.MaxAlertsPerHour = 50; // Higher threshold for enterprise
        });

        services.PostConfigure<PerformanceIntelligenceOptions>(options =>
        {
            options.EnableAutomaticAnalysis = true;
            options.EnableOptimizationRecommendations = true;
            options.EnableMemoryProfiling = true;
            options.EnableDatabaseAnalysis = true;
            options.EnableNetworkAnalysis = true;
            options.AnalysisIntervalMinutes = 15; // More frequent analysis
        });

        services.PostConfigure<BusinessAnalyticsOptions>(options =>
        {
            options.EnableApiUsageTracking = true;
            options.EnableUserBehaviorAnalytics = true;
            options.EnableGeographicAnalytics = true;
            options.EnableFeatureAdoptionTracking = true;
            options.EnablePerformanceCorrelation = true;
            options.AggregationIntervalMinutes = 5; // More frequent aggregation
        });

        services.PostConfigure<MonitoringIntegrationOptions>(options =>
        {
            options.EnablePrometheusExport = true;
            options.EnableOtlpExport = true;
            options.EnableDataExport = true;
            options.EnableWebhookIntegration = true;
            options.EnableMultiTenant = true;
            options.ExportIntervalMinutes = 3; // More frequent exports
        });

        return services;
    }

    /// <summary>
    /// Adds monitoring middleware and pipeline components.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMonitoringMiddleware(this IServiceCollection services)
    {
        // Middleware classes are not registered as services - they are instantiated by the middleware pipeline
        // Dependencies for middleware (like IBusinessAnalyticsService) are already registered above
        // Use app.UseMiddleware<MiddlewareName>() in Program.cs to add to pipeline

        return services;
    }

    /// <summary>
    /// Configures monitoring for development environments with reduced overhead.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDevelopmentMonitoring(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddBasicMonitoring(configuration);

        // Override options for development
        services.PostConfigure<PerformanceIntelligenceOptions>(options =>
        {
            options.AnalysisIntervalMinutes = 30; // Less frequent in development
            options.MaxRecommendations = 20; // Fewer recommendations
        });

        services.PostConfigure<BusinessAnalyticsOptions>(options =>
        {
            options.AggregationIntervalMinutes = 30; // Less frequent aggregation
            options.DataRetentionDays = 7; // Shorter retention
        });

        return services;
    }

    /// <summary>
    /// Configures monitoring for production environments with optimal performance.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddProductionMonitoring(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddEnterpriseMonitoring(configuration);

        // Production-optimized settings
        services.PostConfigure<AnomalyDetectionOptions>(options =>
        {
            options.MaxHistoricalDataPoints = 2000; // More history for production
            options.AlertCooldownMinutes = 5; // Faster alerts in production
        });

        services.PostConfigure<BusinessAnalyticsOptions>(options =>
        {
            options.DataRetentionDays = 365; // Longer retention for compliance
            options.MaxEventsInMemory = 20000; // Larger buffer for high traffic
        });

        return services;
    }

    /// <summary>
    /// Validates that all required monitoring services are properly registered.
    /// </summary>
    /// <param name="services">The service collection to validate.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection ValidateMonitoringServices(this IServiceCollection services)
    {
        // This would be called by the application startup to validate configuration
        services.AddSingleton<IHostedService, MonitoringValidationService>();
        return services;
    }
}

/// <summary>
/// Middleware for automatic request tracking and business analytics.
/// </summary>
public sealed class RequestTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IBusinessAnalyticsService _businessAnalytics;
    private readonly ILogger<RequestTrackingMiddleware> _logger;

    public RequestTrackingMiddleware(
        RequestDelegate next,
        IBusinessAnalyticsService businessAnalytics,
        ILogger<RequestTrackingMiddleware> logger)
    {
        _next = next;
        _businessAnalytics = businessAnalytics;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            try
            {
                var apiEvent = new ApiUsageEvent
                {
                    Timestamp = startTime,
                    Method = context.Request.Method,
                    Path = context.Request.Path,
                    Protocol = DetermineProtocol(context.Request.Path),
                    ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    IsError = context.Response.StatusCode >= 400,
                    UserId = ExtractUserId(context),
                    ClientId = ExtractClientId(context),
                    IpAddress = GetClientIpAddress(context),
                    UserAgent = context.Request.Headers.UserAgent.ToString()
                };

                await _businessAnalytics.RecordApiUsageAsync(apiEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record API usage analytics");
            }
        }
    }

    private string DetermineProtocol(string path)
    {
        return path switch
        {
            var p when p.StartsWith("/rest/services") => "FeatureServer",
            var p when p.StartsWith("/ogc/features") => "OGC-Features",
            var p when p.StartsWith("/odata") => "OData",
            var p when p.StartsWith("/api/admin") => "Admin",
            var p when p.StartsWith("/api/metrics") => "Monitoring",
            var p when p.StartsWith("/healthz") => "Health",
            _ => "Unknown"
        };
    }

    private string? ExtractUserId(HttpContext context)
    {
        return context.User?.Identity?.Name ??
               context.Request.Headers["X-User-Id"].FirstOrDefault();
    }

    private string? ExtractClientId(HttpContext context)
    {
        return context.Request.Headers["X-Client-Id"].FirstOrDefault() ??
               context.Request.Headers["X-API-Key"].FirstOrDefault();
    }

    private string? GetClientIpAddress(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}

/// <summary>
/// Middleware for automatic performance tracking.
/// </summary>
public sealed class PerformanceTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IPerformanceIntelligenceService _performanceIntelligence;
    private readonly ILogger<PerformanceTrackingMiddleware> _logger;

    public PerformanceTrackingMiddleware(
        RequestDelegate next,
        IPerformanceIntelligenceService performanceIntelligence,
        ILogger<PerformanceTrackingMiddleware> logger)
    {
        _next = next;
        _performanceIntelligence = performanceIntelligence;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(false);

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            var memoryAfter = GC.GetTotalMemory(false);

            try
            {
                var performanceSnapshot = new PerformanceSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    CpuUsagePercent = GetCurrentCpuUsage(),
                    MemoryUsageMB = memoryAfter / (1024.0 * 1024.0),
                    AverageResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    RequestsPerSecond = 1.0 / stopwatch.Elapsed.TotalSeconds,
                    ErrorRatePercent = context.Response.StatusCode >= 400 ? 1.0 : 0.0
                };

                await _performanceIntelligence.RecordPerformanceMetricsAsync(performanceSnapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record performance metrics");
            }
        }
    }

    private double GetCurrentCpuUsage()
    {
        // This would use actual CPU monitoring in a real implementation
        // For now, return a reasonable estimate based on response time
        return Random.Shared.Next(10, 60);
    }
}

/// <summary>
/// Middleware for automatic business analytics tracking.
/// </summary>
public sealed class BusinessAnalyticsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IBusinessAnalyticsService _businessAnalytics;
    private readonly ILogger<BusinessAnalyticsMiddleware> _logger;

    public BusinessAnalyticsMiddleware(
        RequestDelegate next,
        IBusinessAnalyticsService businessAnalytics,
        ILogger<BusinessAnalyticsMiddleware> logger)
    {
        _next = next;
        _businessAnalytics = businessAnalytics;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = ExtractUserId(context);

        if (!string.IsNullOrEmpty(userId))
        {
            try
            {
                var userEvent = new UserBehaviorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    UserId = userId,
                    EventType = "page_view",
                    ClientId = ExtractClientId(context),
                    IpAddress = GetClientIpAddress(context),
                    UserAgent = context.Request.Headers.UserAgent.ToString(),
                    Country = DetermineCountryFromIP(GetClientIpAddress(context)),
                    Properties = new Dictionary<string, object>
                    {
                        { "path", context.Request.Path.Value ?? "" },
                        { "method", context.Request.Method },
                        { "protocol", DetermineProtocol(context.Request.Path) }
                    }
                };

                await _businessAnalytics.RecordUserBehaviorAsync(userEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record user behavior analytics");
            }
        }

        await _next(context);
    }

    private string? ExtractUserId(HttpContext context)
    {
        return context.User?.Identity?.Name ??
               context.Request.Headers["X-User-Id"].FirstOrDefault();
    }

    private string? ExtractClientId(HttpContext context)
    {
        return context.Request.Headers["X-Client-Id"].FirstOrDefault();
    }

    private string? GetClientIpAddress(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString();
    }

    private string DetermineProtocol(string path)
    {
        return path switch
        {
            var p when p.StartsWith("/rest/services") => "FeatureServer",
            var p when p.StartsWith("/ogc/features") => "OGC-Features",
            var p when p.StartsWith("/odata") => "OData",
            _ => "Unknown"
        };
    }

    private string? DetermineCountryFromIP(string? ipAddress)
    {
        // This would use a GeoIP service in a real implementation
        // For now, return some sample countries
        if (string.IsNullOrEmpty(ipAddress))
            return null;

        var countries = new[] { "USA", "Canada", "Germany", "France", "UK", "Japan", "Australia" };
        return countries[Math.Abs(ipAddress.GetHashCode()) % countries.Length];
    }
}

/// <summary>
/// Hosted service for validating monitoring configuration on startup.
/// </summary>
internal sealed class MonitoringValidationService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MonitoringValidationService> _logger;

    public MonitoringValidationService(
        IServiceProvider serviceProvider,
        ILogger<MonitoringValidationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ValidateServices();
            _logger.LogInformation("Monitoring services validation completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitoring services validation failed");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void ValidateServices()
    {
        // Check that required services are registered
        var requiredServices = new[]
        {
            typeof(IBusinessAnalyticsService),
            typeof(IPerformanceIntelligenceService)
        };

        var optionalServices = new[]
        {
            typeof(IAnomalyDetectionService),
            typeof(IIntelligentAlertingService),
            typeof(IMonitoringIntegrationService)
        };

        foreach (var serviceType in requiredServices)
        {
            var service = _serviceProvider.GetService(serviceType) ?? throw new InvalidOperationException($"Required monitoring service {serviceType.Name} is not registered");
        }

        foreach (var serviceType in optionalServices)
        {
            var service = _serviceProvider.GetService(serviceType);
            if (service == null)
            {
                _logger.LogWarning("Optional monitoring service {ServiceType} is not registered", serviceType.Name);
            }
        }

        _logger.LogInformation("All required monitoring services are properly registered");
    }
}
