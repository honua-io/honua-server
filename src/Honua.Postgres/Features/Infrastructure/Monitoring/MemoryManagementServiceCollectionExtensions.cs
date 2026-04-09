// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Infrastructure.Monitoring;

/// <summary>
/// Service collection extensions for memory management services
/// </summary>
public static class MemoryManagementServiceCollectionExtensions
{
    /// <summary>
    /// Registers memory management services with the DI container
    /// </summary>
    public static IServiceCollection AddMemoryManagement(
        this IServiceCollection services,
        Action<MemoryManagementOptions>? configureOptions = null)
    {
        // Register configuration
        services.Configure<MemoryManagementOptions>(options =>
        {
            configureOptions?.Invoke(options);
            options.Validate();
        });

        // Register memory monitor as singleton for global memory tracking
        services.AddSingleton<IMemoryMonitor, ProductionMemoryMonitor>();

        // Register hosted service for background memory monitoring
        services.AddHostedService<MemoryMonitorHostedService>();

        return services;
    }
}

/// <summary>
/// Hosted service that manages memory monitoring lifecycle
/// </summary>
internal sealed class MemoryMonitorHostedService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly IMemoryMonitor _memoryMonitor;
    private readonly IOptions<MemoryManagementOptions> _options;
    private readonly ILogger<MemoryMonitorHostedService> _logger;

    public MemoryMonitorHostedService(
        IMemoryMonitor memoryMonitor,
        IOptions<MemoryManagementOptions> options,
        ILogger<MemoryMonitorHostedService> logger)
    {
        _memoryMonitor = memoryMonitor ?? throw new ArgumentNullException(nameof(memoryMonitor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.Value.CacheCleanupIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);

                // Check memory pressure and take action if needed
                if (_memoryMonitor.IsMemoryPressureHigh() && _options.Value.EnableAutoMemoryRelief)
                {
                    var relieved = await _memoryMonitor.TryRelieveMemoryPressureAsync();
                    if (relieved)
                    {
                        _logger.LogInformation("Memory pressure relief operation completed successfully");
                    }
                }

                // Log memory stats periodically for monitoring
                var stats = _memoryMonitor.GetMemoryUsage();
                if (stats.IsUnderMemoryPressure)
                {
                    _logger.LogWarning("System is under memory pressure - TotalMemory={TotalMemory:N0}, WorkingSet={WorkingSet:N0}",
                        stats.TotalMemoryBytes, stats.WorkingSetBytes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during memory monitoring cycle");
            }
        }
    }
}
