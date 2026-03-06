// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// Note: Honua.Core.Sdk services would be added here when package is available
using Honua.Core.Transport.Clients;
using Honua.Mobile.Sdk.Clients;
using Honua.Mobile.Sdk.Storage;

namespace Honua.Mobile.Sdk;

/// <summary>
/// Service collection extensions for registering Honua mobile services.
/// </summary>
public static class HonuaMobileServiceCollectionExtensions
{
    /// <summary>
    /// Adds Honua mobile services with default configuration.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="serverAddress">Honua server address</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHonuaMobile(
        this IServiceCollection services,
        string serverAddress)
    {
        return services.AddHonuaMobile(options =>
        {
            options.ServerAddress = serverAddress;
        });
    }

    /// <summary>
    /// Adds Honua mobile services with configuration.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHonuaMobile(
        this IServiceCollection services,
        Action<HonuaMobileClientOptions> configureOptions)
    {
        // Configure options
        services.Configure(configureOptions);

        // Note: Add Honua Core SDK services when package is available
        // services.AddHonuaCore(...);

        // Add Entity Framework context for offline storage
        services.AddDbContext<OfflineDbContext>((provider, options) =>
        {
            var clientOptions = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>().Value;
            var databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                clientOptions.OfflineDatabase);

            options.UseSqlite($"Data Source={databasePath}");

            // Enable sensitive data logging in debug builds
#if DEBUG
            options.EnableSensitiveDataLogging();
#endif
        });

        // Register mobile-specific services
        services.TryAddSingleton<IConnectivityService, MauiConnectivityService>();
        services.TryAddScoped<IOfflineStorageService, SqliteOfflineStorageService>();

        // Note: Register mobile gRPC client adapter when core client is available
        // services.TryAddScoped<IFeatureServiceClient<MobileContext>>(...);

        // For now, register a mock implementation
        services.TryAddScoped<IFeatureServiceClient<MobileContext>, MockMobileFeatureServiceClient>();

        // Register the main mobile client
        services.TryAddScoped<HonuaMobileClient>();

        return services;
    }

    /// <summary>
    /// Adds Honua mobile services with advanced configuration.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Configuration action</param>
    /// <param name="configureDbContext">Database context configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHonuaMobile(
        this IServiceCollection services,
        Action<HonuaMobileClientOptions> configureOptions,
        Action<DbContextOptionsBuilder>? configureDbContext = null)
    {
        // Configure options
        services.Configure(configureOptions);

        // Add Honua Core SDK services
        services.AddHonuaCore(provider =>
        {
            var options = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>().Value;
            return new HonuaCoreOptions
            {
                ServerAddress = options.ServerAddress,
                ApiKey = options.ApiKey,
                RequestTimeout = options.RequestTimeout
            };
        });

        // Add Entity Framework context with custom configuration
        if (configureDbContext != null)
        {
            services.AddDbContext<OfflineDbContext>(configureDbContext);
        }
        else
        {
            services.AddDbContext<OfflineDbContext>((provider, options) =>
            {
                var clientOptions = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>().Value;
                var databasePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    clientOptions.OfflineDatabase);

                options.UseSqlite($"Data Source={databasePath}");
            });
        }

        // Register services
        services.TryAddSingleton<IConnectivityService, MauiConnectivityService>();
        services.TryAddScoped<IOfflineStorageService, SqliteOfflineStorageService>();

        services.TryAddScoped<IFeatureServiceClient<MobileContext>>(provider =>
        {
            var coreClient = provider.GetRequiredService<IFeatureServiceClient>();
            var options = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>();
            var logger = provider.GetRequiredService<ILogger<MobileFeatureServiceClient>>();

            return new MobileFeatureServiceClient(coreClient, options, logger);
        });

        services.TryAddScoped<HonuaMobileClient>();

        return services;
    }

    /// <summary>
    /// Initializes the mobile database and applies any pending migrations.
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the operation</returns>
    public static async Task InitializeMobileDatabaseAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OfflineDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<OfflineDbContext>>();

        try
        {
            logger.LogInformation("Initializing mobile database...");

            // Ensure database is created and apply any pending migrations
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            // If using migrations (recommended for production), use this instead:
            // await dbContext.Database.MigrateAsync(cancellationToken);

            logger.LogInformation("Mobile database initialized successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize mobile database");
            throw;
        }
    }

    /// <summary>
    /// Performs cleanup of old offline data based on retention policies.
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cleanup results</returns>
    public static async Task<CleanupResult> CleanupOfflineDataAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var storageService = scope.ServiceProvider.GetRequiredService<IOfflineStorageService>();

        return await storageService.CleanupOldDataAsync(cancellationToken);
    }

    /// <summary>
    /// Gets storage statistics for the mobile offline database.
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Storage statistics</returns>
    public static async Task<StorageStatistics> GetMobileStorageStatisticsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var storageService = scope.ServiceProvider.GetRequiredService<IOfflineStorageService>();

        return await storageService.GetStorageStatisticsAsync(cancellationToken);
    }
}

/// <summary>
/// Configuration options for Honua Core SDK integration.
/// This is a placeholder - the real class should come from Honua.Core.Sdk.
/// </summary>
public class HonuaCoreOptions
{
    /// <summary>
    /// Server address.
    /// </summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Request timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}