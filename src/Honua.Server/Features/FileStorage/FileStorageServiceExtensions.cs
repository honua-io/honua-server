// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FileStorage.Abstractions;
using Honua.Core.Features.FileStorage.Domain;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Extension methods for registering file storage services
/// </summary>
public static class FileStorageServiceExtensions
{
    /// <summary>
    /// Adds cloud file storage services to the dependency injection container
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration for binding options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddCloudFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration
        var section = configuration.GetSection("FileStorage");
        services.Configure<CloudStorageOptions>(section);

        // Bind provider-specific options
        var localSection = section.GetSection("LocalStorage");
        if (localSection.Exists())
        {
            services.Configure<LocalStorageOptions>(localSection);
        }
        else
        {
            // Default local storage path if not configured
            services.Configure<LocalStorageOptions>(options =>
            {
                options = new LocalStorageOptions
                {
                    BasePath = Path.Combine(Path.GetTempPath(), "honua-storage"),
                    CreateDirectoryIfNotExists = true
                };
            });
            services.AddSingleton(new LocalStorageOptions
            {
                BasePath = section.GetValue("LocalStorage:BasePath", null as string)
                           ?? Path.Combine(Path.GetTempPath(), "honua-storage"),
                CreateDirectoryIfNotExists = section.GetValue("LocalStorage:CreateDirectoryIfNotExists", true)
            });
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<LocalStorageOptions>>(sp =>
                Microsoft.Extensions.Options.Options.Create(sp.GetRequiredService<LocalStorageOptions>()));
        }

        // Determine provider from configuration or environment
        var providerName = section.GetValue<string>("Provider")
                           ?? Environment.GetEnvironmentVariable("HONUA_STORAGE_PROVIDER")
                           ?? "Local";

        var provider = Enum.TryParse<CloudStorageProvider>(providerName, ignoreCase: true, out var p)
            ? p
            : CloudStorageProvider.Local;

        // Register appropriate provider
        switch (provider)
        {
            case CloudStorageProvider.Local:
                services.AddSingleton<ICloudFileStorage, LocalFileStorage>();
                break;

            case CloudStorageProvider.AwsS3:
                // TODO: Sprint 2 - Add AWS S3 implementation
                throw new NotSupportedException(
                    "AWS S3 storage provider is not yet implemented. " +
                    "Use 'Local' provider for development or wait for Sprint 2 implementation.");

            case CloudStorageProvider.AzureBlob:
                // TODO: Sprint 3 - Add Azure Blob implementation
                throw new NotSupportedException(
                    "Azure Blob storage provider is not yet implemented. " +
                    "Use 'Local' provider for development or wait for Sprint 3 implementation.");

            case CloudStorageProvider.GoogleCloudStorage:
                // TODO: Sprint 3 - Add GCS implementation
                throw new NotSupportedException(
                    "Google Cloud Storage provider is not yet implemented. " +
                    "Use 'Local' provider for development or wait for Sprint 3 implementation.");

            default:
                throw new InvalidOperationException($"Unknown storage provider: {providerName}");
        }

        // Register cleanup background service
        var enableCleanup = section.GetValue("EnableAutomaticCleanup", true);
        if (enableCleanup)
        {
            services.AddHostedService<FileStorageCleanupService>();
        }

        return services;
    }

    /// <summary>
    /// Adds cloud file storage services with a specific provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configure">Configuration action for options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddCloudFileStorage(
        this IServiceCollection services,
        Action<CloudStorageOptions> configure)
    {
        var options = new CloudStorageOptions();
        configure(options);

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        // Configure local storage options if using local provider
        if (options.Provider == CloudStorageProvider.Local && options.LocalStorage is not null)
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options.LocalStorage));
        }
        else if (options.Provider == CloudStorageProvider.Local)
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new LocalStorageOptions
            {
                BasePath = Path.Combine(Path.GetTempPath(), "honua-storage"),
                CreateDirectoryIfNotExists = true
            }));
        }

        switch (options.Provider)
        {
            case CloudStorageProvider.Local:
                services.AddSingleton<ICloudFileStorage, LocalFileStorage>();
                break;

            case CloudStorageProvider.AwsS3:
            case CloudStorageProvider.AzureBlob:
            case CloudStorageProvider.GoogleCloudStorage:
                throw new NotSupportedException(
                    $"{options.Provider} storage provider is not yet implemented. " +
                    "Use 'Local' provider for development.");

            default:
                throw new InvalidOperationException($"Unknown storage provider: {options.Provider}");
        }

        if (options.EnableAutomaticCleanup)
        {
            services.AddHostedService<FileStorageCleanupService>();
        }

        return services;
    }
}
