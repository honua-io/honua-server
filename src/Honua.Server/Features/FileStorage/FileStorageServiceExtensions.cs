// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        // Register upload progress store
        services.AddSingleton<IUploadProgressStore, InMemoryUploadProgressStore>();

        // Bind configuration
        var section = configuration.GetSection("FileStorage");
        services.Configure<CloudStorageOptions>(section);
        services.PostConfigure<CloudStorageOptions>(ResolveCloudStorageSecrets);

        // Bind provider-specific options
        var localSection = section.GetSection("LocalStorage");
        if (localSection.Exists())
        {
            services.Configure<LocalStorageOptions>(localSection);
        }
        else
        {
            // Default local storage path if not configured
            var defaultLocalOptions = new LocalStorageOptions
            {
                BasePath = section.GetValue("LocalStorage:BasePath", null as string)
                           ?? Path.Combine(Path.GetTempPath(), "honua-storage"),
                CreateDirectoryIfNotExists = section.GetValue("LocalStorage:CreateDirectoryIfNotExists", true)
            };
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<LocalStorageOptions>>(
                Microsoft.Extensions.Options.Options.Create(defaultLocalOptions));
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
                services.AddSingleton<ICloudFileStorage, AwsS3FileStorage>();
                break;

            case CloudStorageProvider.AzureBlob:
                services.AddSingleton<ICloudFileStorage, AzureBlobFileStorage>();
                break;

            case CloudStorageProvider.GoogleCloudStorage:
                services.AddSingleton<ICloudFileStorage, GoogleCloudStorageFileStorage>();
                break;

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
        // Register upload progress store
        services.AddSingleton<IUploadProgressStore, InMemoryUploadProgressStore>();

        var options = new CloudStorageOptions();
        configure(options);
        ResolveCloudStorageSecrets(options);

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
                services.AddSingleton<ICloudFileStorage, AwsS3FileStorage>();
                break;

            case CloudStorageProvider.AzureBlob:
                services.AddSingleton<ICloudFileStorage, AzureBlobFileStorage>();
                break;

            case CloudStorageProvider.GoogleCloudStorage:
                services.AddSingleton<ICloudFileStorage, GoogleCloudStorageFileStorage>();
                break;

            default:
                throw new InvalidOperationException($"Unknown storage provider: {options.Provider}");
        }

        if (options.EnableAutomaticCleanup)
        {
            services.AddHostedService<FileStorageCleanupService>();
        }

        return services;
    }

    private static void ResolveCloudStorageSecrets(CloudStorageOptions options)
    {
        if (options.AwsS3 != null)
        {
            options.AwsS3.AccessKeyId = SecretReferenceResolver.ResolveEnvironmentReference(
                options.AwsS3.AccessKeyId,
                "FileStorage:AwsS3:AccessKeyId");
            options.AwsS3.SecretAccessKey = SecretReferenceResolver.ResolveEnvironmentReference(
                options.AwsS3.SecretAccessKey,
                "FileStorage:AwsS3:SecretAccessKey");
        }

        if (options.AzureBlob is { } azureBlob)
        {
            azureBlob.ConnectionString = SecretReferenceResolver.ResolveEnvironmentReference(
                azureBlob.ConnectionString,
                "FileStorage:AzureBlob:ConnectionString") ?? string.Empty;
        }

        if (options.GoogleCloudStorage is { } googleCloudStorage)
        {
            googleCloudStorage.CredentialsPath = SecretReferenceResolver.ResolveEnvironmentReference(
                googleCloudStorage.CredentialsPath,
                "FileStorage:GoogleCloudStorage:CredentialsPath");
        }
    }
}
