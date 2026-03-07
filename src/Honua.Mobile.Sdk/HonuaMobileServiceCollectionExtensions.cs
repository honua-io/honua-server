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

using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
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
        return services.AddHonuaMobileServices(configureDbContext: null);
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
        return services.AddHonuaMobileServices(configureDbContext);
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

    private static IServiceCollection AddHonuaMobileServices(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder>? configureDbContext)
    {
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

#if DEBUG
                options.EnableSensitiveDataLogging();
#endif
            });
        }

        services.TryAddSingleton<IConnectivityService, MauiConnectivityService>();
        services.TryAddScoped<IOfflineStorageService, SqliteOfflineStorageService>();
        services.TryAddScoped<IFeatureServiceClient<object>>(CreateCoreGrpcClient);
        services.TryAddScoped<IFeatureServiceClient<MobileContext>>(provider =>
        {
            var coreClient = provider.GetRequiredService<IFeatureServiceClient<object>>();
            var options = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>();
            var logger = provider.GetRequiredService<ILogger<MobileFeatureServiceClient>>();
            return new MobileFeatureServiceClient(coreClient, options, logger);
        });
        services.TryAddScoped<HonuaMobileClient>();

        return services;
    }

    private static IFeatureServiceClient<object> CreateCoreGrpcClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>().Value;
        var logger = provider.GetService<ILogger<GrpcFeatureServiceClient<object>>>();
        var grpcOptions = new GrpcClientOptions
        {
            RequestTimeout = options.RequestTimeout,
            StreamTimeout = options.StreamingTimeout
        };

        return new GrpcFeatureServiceClient<object>(
            context => CreateGrpcFeatureServiceClient(options, context),
            grpcOptions,
            logger);
    }

    private static Geospatial.V1.FeatureService.FeatureServiceClient CreateGrpcFeatureServiceClient(
        HonuaMobileClientOptions options,
        object context)
    {
        if (!Uri.TryCreate(options.ServerAddress, UriKind.Absolute, out var serverAddress))
        {
            throw new InvalidOperationException("Honua mobile client requires an absolute ServerAddress.");
        }

        var httpClient = CreateHttpClient(serverAddress, options, context as IReadOnlyDictionary<string, object>);
        var channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions { HttpClient = httpClient });
        return new Geospatial.V1.FeatureService.FeatureServiceClient(channel);
    }

    private static HttpClient CreateHttpClient(
        Uri serverAddress,
        HonuaMobileClientOptions options,
        IReadOnlyDictionary<string, object>? context)
    {
        var httpClient = new HttpClient(new HttpClientHandler())
        {
            BaseAddress = serverAddress,
            Timeout = ResolveTimeout(options, context)
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(options.BearerToken))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.BearerToken);
        }

        foreach (var header in options.CustomHeaders)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (context != null &&
            context.TryGetValue("headers", out var headersValue) &&
            headersValue is IReadOnlyDictionary<string, string> headers)
        {
            foreach (var header in headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return httpClient;
    }

    private static TimeSpan ResolveTimeout(
        HonuaMobileClientOptions options,
        IReadOnlyDictionary<string, object>? context)
    {
        if (context != null &&
            context.TryGetValue("timeout", out var timeoutValue) &&
            timeoutValue is TimeSpan timeout)
        {
            return timeout;
        }

        return options.RequestTimeout;
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
