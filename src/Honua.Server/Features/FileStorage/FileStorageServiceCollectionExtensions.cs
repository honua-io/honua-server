// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Honua.FileStorage;

/// <summary>
/// Composition-root wiring that selects the file-storage backend (local disk
/// from Honua.Io, S3 from Honua.Aws, Azure Blob from Honua.Azure) and registers
/// the upload-progress store and retention cleanup. Lives in Server because it
/// is the only assembly that references all three backends.
/// </summary>
public static class FileStorageServiceCollectionExtensions
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
        RegisterUploadProgressStore(services);

        // Bind configuration
        var section = configuration.GetSection("FileStorage");
        services.Configure<CloudStorageOptions>(section);
        services.Configure<RasterOutputPublicationOptions>(
            configuration.GetSection(RasterOutputPublicationOptions.SectionName));
        services.PostConfigure<CloudStorageOptions>(options =>
        {
            ResolveCloudStorageSecrets(options);
            EnsureLocalStorageDefaults(options, section);
        });

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
                // Path.Combine is safe here: Path.GetTempPath() is the OS temp directory and
                // "honua-storage" is a fixed relative literal, neither externally controlled.
                BasePath = section.GetValue("LocalStorage:BasePath", null as string)
                           ?? Path.Join(Path.GetTempPath(), "honua-storage"),
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
                services.AddSingleton<LocalRasterOutputObjectStore>();
                services.AddSingleton<IRasterOutputObjectStore>(providerServices =>
                    providerServices.GetRequiredService<LocalRasterOutputObjectStore>());
                services.AddSingleton<IRasterOutputManifestStore>(providerServices =>
                    providerServices.GetRequiredService<LocalRasterOutputObjectStore>());
                break;

            case CloudStorageProvider.AwsS3:
#if HONUA_EXCLUDE_AWS
                throw CreateUnavailableProviderException("AWS S3", "HonuaIncludeAws");
#else
                services.AddSingleton<ICloudFileStorage, AwsS3FileStorage>();
                services.AddAwsRasterOutputStorage();
                break;
#endif

            case CloudStorageProvider.AzureBlob:
#if HONUA_EXCLUDE_AZURE
                throw CreateUnavailableProviderException("Azure Blob", "HonuaIncludeAzure");
#else
                services.AddSingleton<ICloudFileStorage, AzureBlobFileStorage>();
                services.AddAzureRasterOutputStorage();
                break;
#endif

            default:
                throw new InvalidOperationException($"Unknown storage provider: {providerName}");
        }

        // Register cleanup background service
        var enableCleanup = section.GetValue("EnableAutomaticCleanup", true);
        RegisterFileStorageCleanup(
            services,
            enableCleanup,
            Honua.Core.Features.ControlPlane.Abstractions.ControlPlaneTriggerModeResolver
                .ShouldHostInProcessTimers(configuration));
        RegisterRasterOutputReconciliation(
            services,
            Honua.Core.Features.ControlPlane.Abstractions.ControlPlaneTriggerModeResolver
                .ShouldHostInProcessTimers(configuration));

        return services;
    }

    /// <summary>
    /// Registers the file-storage cleanup as a PERIODIC control-plane tick (bucket-b). The cleanup
    /// deletes already-expired files via a fresh scope and is idempotent, so the scheduled-tick
    /// handler is registered in BOTH trigger modes; the in-process timer is hosted only when
    /// <paramref name="hostInProcessTimer"/> is set (TriggerMode=Poll, default/on-prem), keeping that
    /// path byte-for-byte unchanged. On AWS this tick can alternatively be replaced by an S3 lifecycle
    /// policy (cheaper); the tick is kept for on-prem/portability.
    /// </summary>
    private static void RegisterFileStorageCleanup(
        IServiceCollection services,
        bool enableCleanup,
        bool hostInProcessTimer)
    {
        if (!enableCleanup)
        {
            return;
        }

        services.TryAddSingleton<FileStorageCleanupService>();
        services.AddSingleton<
            Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler,
            FileStorageCleanupScheduledTickHandler>();
        if (hostInProcessTimer)
        {
            services.AddHostedService(sp => sp.GetRequiredService<FileStorageCleanupService>());
        }
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
        RegisterUploadProgressStore(services);

        var options = new CloudStorageOptions();
        configure(options);
        ResolveCloudStorageSecrets(options);

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.TryAddSingleton<Microsoft.Extensions.Options.IOptions<RasterOutputPublicationOptions>>(
            Microsoft.Extensions.Options.Options.Create(new RasterOutputPublicationOptions()));

        // Configure local storage options if using local provider
        if (options.Provider == CloudStorageProvider.Local && options.LocalStorage is not null)
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options.LocalStorage));
        }
        else if (options.Provider == CloudStorageProvider.Local)
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new LocalStorageOptions
            {
                // Path.Combine is safe here: Path.GetTempPath() is the OS temp directory and
                // "honua-storage" is a fixed relative literal, neither externally controlled.
                BasePath = Path.Join(Path.GetTempPath(), "honua-storage"),
                CreateDirectoryIfNotExists = true
            }));
        }

        switch (options.Provider)
        {
            case CloudStorageProvider.Local:
                services.AddSingleton<ICloudFileStorage, LocalFileStorage>();
                services.AddSingleton<LocalRasterOutputObjectStore>();
                services.AddSingleton<IRasterOutputObjectStore>(providerServices =>
                    providerServices.GetRequiredService<LocalRasterOutputObjectStore>());
                services.AddSingleton<IRasterOutputManifestStore>(providerServices =>
                    providerServices.GetRequiredService<LocalRasterOutputObjectStore>());
                break;

            case CloudStorageProvider.AwsS3:
#if HONUA_EXCLUDE_AWS
                throw CreateUnavailableProviderException("AWS S3", "HonuaIncludeAws");
#else
                services.AddSingleton<ICloudFileStorage, AwsS3FileStorage>();
                services.AddAwsRasterOutputStorage();
                break;
#endif

            case CloudStorageProvider.AzureBlob:
#if HONUA_EXCLUDE_AZURE
                throw CreateUnavailableProviderException("Azure Blob", "HonuaIncludeAzure");
#else
                services.AddSingleton<ICloudFileStorage, AzureBlobFileStorage>();
                services.AddAzureRasterOutputStorage();
                break;
#endif

            default:
                throw new InvalidOperationException($"Unknown storage provider: {options.Provider}");
        }

        // This Action-based overload has no IConfiguration to read the trigger mode from; it is the
        // programmatic/test composition path, which is always poll-style (in-process timer hosted).
        // The IConfiguration overload above is the production path that honors TriggerMode=Event.
        RegisterFileStorageCleanup(services, options.EnableAutomaticCleanup, hostInProcessTimer: true);
        RegisterRasterOutputReconciliation(services, hostInProcessTimer: true);

        return services;
    }

    private static void RegisterRasterOutputReconciliation(
        IServiceCollection services,
        bool hostInProcessTimer)
    {
        services.TryAddSingleton<RasterOutputReconciliationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler,
            RasterOutputReconciliationScheduledTickHandler>());
        if (hostInProcessTimer)
        {
            services.AddHostedService(providerServices =>
                providerServices.GetRequiredService<RasterOutputReconciliationService>());
        }
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

    }

    private static void EnsureLocalStorageDefaults(CloudStorageOptions options, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(section);

        if (options.Provider != CloudStorageProvider.Local)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.LocalStorage?.BasePath))
        {
            return;
        }

        options.LocalStorage = new LocalStorageOptions
        {
            BasePath = section.GetValue("LocalStorage:BasePath", null as string)
                       ?? Path.Join(Path.GetTempPath(), "honua-storage"),
            CreateDirectoryIfNotExists = section.GetValue("LocalStorage:CreateDirectoryIfNotExists", true)
        };
    }

    private static void RegisterUploadProgressStore(IServiceCollection services)
    {
        services.AddSingleton<IUploadProgressStore>(serviceProvider =>
        {
            var universalStore = serviceProvider.GetService<IUniversalProgressStore>();
            if (universalStore != null)
            {
                return new UniversalUploadProgressStore(universalStore);
            }

            return new InMemoryUploadProgressStore();
        });
    }

    private static InvalidOperationException CreateUnavailableProviderException(
        string providerName,
        string includeProperty)
        => new(
            $"{providerName} file storage is not available in this Honua build. " +
            $"Rebuild with -p:{includeProperty}=true or use HonuaBuildProfile=full.");
}
