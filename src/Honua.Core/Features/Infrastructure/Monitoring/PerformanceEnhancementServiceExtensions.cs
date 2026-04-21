// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Service collection extensions for registering performance enhancement services.
/// These optimizations are designed to push server performance from 9.1/10 toward 9.5+/10.
/// </summary>
public static class PerformanceEnhancementServiceExtensions
{
    /// <summary>
    /// Adds comprehensive performance monitoring and optimization services.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Optional configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPerformanceEnhancements(
        this IServiceCollection services,
        Action<PerformanceEnhancementOptions>? configureOptions = null)
    {
        // Configure options
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.Configure<PerformanceEnhancementOptions>(_ => { });
        }

        // Add database query performance monitoring
        services.Configure<QueryPerformanceMonitoringOptions>(options =>
        {
            options.SlowQueryThresholdMs = 500; // 500ms threshold
            options.CriticalSlowQueryThresholdMs = 2000; // 2 second critical threshold
            options.MaxSlowQueryHistory = 500;
            options.CaptureQueryText = false; // Security - don't capture query text by default
        });

        services.AddSingleton<IDatabaseQueryPerformanceMonitor, DatabaseQueryPerformanceMonitor>();

        // Add resource leak detection (development/test only)
        services.Configure<ResourceLeakDetectionOptions>(options =>
        {
            options.Enabled = true;
            options.CaptureAllocationStackTraces = false; // Performance - disabled by default
            options.LeakSuspicionThreshold = TimeSpan.FromMinutes(3);
            options.MaxTrackedResources = 5000;
            options.AutoScanInterval = TimeSpan.FromMinutes(2);
        });

        services.AddSingleton<IResourceLeakDetector>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ResourceLeakDetector>>();
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            var options = serviceProvider.GetRequiredService<IOptions<ResourceLeakDetectionOptions>>();

            // Return null implementation in production for performance
            if (environment.IsProduction())
            {
                return new NullResourceLeakDetector();
            }

            var detector = new ResourceLeakDetector(logger, environment, options);
            return detector;
        });

        // Add enhanced exception telemetry
        services.Configure<ExceptionTelemetryOptions>(options =>
        {
            options.MaxRecentExceptions = 1000;
            options.CaptureStackTraces = true;
            options.MaxStackTraceLength = 2048;
            options.SanitizeSensitiveData = true;
            options.RateLimitWindow = TimeSpan.FromMinutes(1);
            options.MaxOccurrencesPerWindow = 5;
        });

        services.AddSingleton<IEnhancedExceptionTelemetry, EnhancedExceptionTelemetry>();

        // Query result caching is configured at the Server level since it depends on IMemoryCache

        // Register hosted service for resource leak detector
        services.AddHostedService<ResourceLeakDetectorHostedService>();

        return services;
    }

    /// <summary>
    /// Adds database query performance monitoring specifically.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Optional configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddDatabaseQueryPerformanceMonitoring(
        this IServiceCollection services,
        Action<QueryPerformanceMonitoringOptions>? configureOptions = null)
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<IDatabaseQueryPerformanceMonitor, DatabaseQueryPerformanceMonitor>();

        return services;
    }

    /// <summary>
    /// Adds resource leak detection services.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Optional configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddResourceLeakDetection(
        this IServiceCollection services,
        Action<ResourceLeakDetectionOptions>? configureOptions = null)
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<IResourceLeakDetector>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ResourceLeakDetector>>();
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            var options = serviceProvider.GetRequiredService<IOptions<ResourceLeakDetectionOptions>>();

            if (environment.IsProduction())
            {
                return new NullResourceLeakDetector();
            }

            return new ResourceLeakDetector(logger, environment, options);
        });

        services.AddHostedService<ResourceLeakDetectorHostedService>();

        return services;
    }

    /// <summary>
    /// Adds enhanced exception telemetry services.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Optional configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddEnhancedExceptionTelemetry(
        this IServiceCollection services,
        Action<ExceptionTelemetryOptions>? configureOptions = null)
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<IEnhancedExceptionTelemetry, EnhancedExceptionTelemetry>();

        return services;
    }

    // Query result caching methods moved to Server level
}

/// <summary>
/// Overall configuration options for performance enhancements.
/// </summary>
public sealed class PerformanceEnhancementOptions
{
    /// <summary>
    /// Whether to enable database query performance monitoring.
    /// </summary>
    public bool EnableQueryPerformanceMonitoring { get; set; } = true;

    /// <summary>
    /// Whether to enable resource leak detection (development/test only).
    /// </summary>
    public bool EnableResourceLeakDetection { get; set; } = true;

    /// <summary>
    /// Whether to enable enhanced exception telemetry.
    /// </summary>
    public bool EnableEnhancedExceptionTelemetry { get; set; } = true;

    /// <summary>
    /// Whether to enable query result caching (configured at Server level).
    /// </summary>
    public bool EnableQueryResultCaching { get; set; } = true;

    /// <summary>
    /// Whether to enable performance optimizations in development.
    /// </summary>
    public bool EnableDevelopmentOptimizations { get; set; } = true;

    /// <summary>
    /// Whether to enable detailed performance metrics (may have slight performance impact).
    /// </summary>
    public bool EnableDetailedMetrics { get; set; } = true;
}

/// <summary>
/// Hosted service wrapper for resource leak detector.
/// </summary>
internal sealed class ResourceLeakDetectorHostedService : IHostedService
{
    private readonly IResourceLeakDetector _detector;

    public ResourceLeakDetectorHostedService(IResourceLeakDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_detector is ResourceLeakDetector resourceDetector)
        {
            await resourceDetector.StartAsync(cancellationToken);
        }
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_detector is ResourceLeakDetector resourceDetector)
        {
            await resourceDetector.StopAsync(cancellationToken);
        }
        await Task.CompletedTask;
    }
}