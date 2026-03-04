// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace FieldDataCollection.Services;

/// <summary>
/// Service collection extensions for registering local storage services.
/// </summary>
public static class LocalStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers local storage services with GeoPackage backend.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddLocalStorage(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure storage options
        services.Configure<LocalStorageOptions>(configuration.GetSection(LocalStorageOptions.SectionName));

        // Register storage implementation as singleton for shared database connection
        services.AddSingleton<ILocalStorageService>(serviceProvider =>
        {
            var options = configuration.GetSection(LocalStorageOptions.SectionName).Get<LocalStorageOptions>()
                         ?? new LocalStorageOptions();

            var databasePath = GetDatabasePath(options);
            return new GeoPackageLocalStorageService(databasePath);
        });

        // Register sync manager
        services.AddScoped<IOfflineSyncManager, OfflineSyncManager>();

        return services;
    }

    /// <summary>
    /// Registers local storage services with explicit database path.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="geoPackagePath">Path to GeoPackage database file.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddLocalStorage(this IServiceCollection services, string geoPackagePath)
    {
        services.AddSingleton<ILocalStorageService>(_ => new GeoPackageLocalStorageService(geoPackagePath));
        services.AddScoped<IOfflineSyncManager, OfflineSyncManager>();
        return services;
    }

    private static string GetDatabasePath(LocalStorageOptions options)
    {
        if (!string.IsNullOrEmpty(options.DatabasePath))
        {
            return options.DatabasePath;
        }

        // Default to app data directory
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "HonuaFieldData");

        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        return Path.Combine(appFolder, "honua_field_data.gpkg");
    }
}

/// <summary>
/// Configuration options for local storage.
/// </summary>
public class LocalStorageOptions
{
    public const string SectionName = "LocalStorage";

    /// <summary>
    /// Path to the GeoPackage database file.
    /// If not specified, defaults to app data directory.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Whether to enable automatic background sync.
    /// </summary>
    public bool EnableAutoSync { get; set; } = true;

    /// <summary>
    /// Sync interval in minutes for background sync.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Whether to only sync over Wi-Fi connections.
    /// </summary>
    public bool WifiOnlySync { get; set; } = false;

    /// <summary>
    /// Maximum database size in MB before cleanup is triggered.
    /// </summary>
    public int MaxDatabaseSizeMB { get; set; } = 500;

    /// <summary>
    /// Number of days to retain completed submissions locally.
    /// </summary>
    public int RetainCompletedSubmissionsDays { get; set; } = 30;

    /// <summary>
    /// Whether to enable detailed logging for storage operations.
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// Cache timeout in minutes for form definitions.
    /// </summary>
    public int FormCacheTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// Whether to enable spatial indexing for performance.
    /// </summary>
    public bool EnableSpatialIndexing { get; set; } = true;

    /// <summary>
    /// Whether to compress media files when storing locally.
    /// </summary>
    public bool CompressMedia { get; set; } = true;

    /// <summary>
    /// JPEG quality for compressed photos (1-100).
    /// </summary>
    public int PhotoQuality { get; set; } = 85;
}