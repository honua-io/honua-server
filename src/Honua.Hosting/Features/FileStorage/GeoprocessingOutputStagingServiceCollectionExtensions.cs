// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.FileStorage;

public static class GeoprocessingOutputStagingServiceCollectionExtensions
{
    public static IServiceCollection AddGeoprocessingOutputStaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GeoprocessingOutputStagingOptions.SectionName);
        var storageKeyPrefix = GeoprocessingOutputStorageKeyPrefix.FromConfiguration(configuration);
        var fileStorageIsShared = GeoprocessingOutputStorageKeyPrefix.FileStorageProvider(configuration)
            is CloudStorageProvider.AwsS3 or CloudStorageProvider.AzureBlob;
        services
            .AddOptions<GeoprocessingOutputStagingOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => !options.Enabled || IsKnownProvider(options.Provider),
                "Geoprocessing:OutputStaging:Provider must be 'local' or 'object-storage'.")
            .Validate(
                options => !options.Enabled || !IsLocal(options) || GeoprocessingOutputStoreAttestationValidator.IsValid(options),
                GeoprocessingOutputStoreAttestationValidator.FailureMessage)
            .Validate(
                options => !options.Enabled || !IsLocal(options) || !string.IsNullOrWhiteSpace(options.LocalRootPath),
                "Geoprocessing:OutputStaging:LocalRootPath is required when the local staging provider is enabled.")
            .Validate(
                options => !options.Enabled || !IsObjectStorage(options) || fileStorageIsShared,
                "Geoprocessing:OutputStaging:Provider 'object-storage' requires FileStorage:Provider to be AwsS3 or AzureBlob.")
            .Validate(
                options => !options.Enabled || !IsObjectStorage(options) || IsUnderStoragePrefix(options.KeyPrefix, storageKeyPrefix),
                "Geoprocessing:OutputStaging:KeyPrefix must start with the file storage key prefix when one is configured.")
            .Validate(
                options => options.SweepInterval > TimeSpan.Zero
                    && options.SweepInterval <= TimeSpan.FromDays(1),
                "Geoprocessing:OutputStaging:SweepInterval must be greater than zero and no more than one day.")
            .Validate(
                options => options.ReadLeaseDuration > TimeSpan.Zero
                    && options.ReadLeaseDuration <= TimeSpan.FromDays(1),
                "Geoprocessing:OutputStaging:ReadLeaseDuration must be greater than zero and no more than one day.")
            .ValidateOnStart();

        if (!section.GetValue<bool>(nameof(GeoprocessingOutputStagingOptions.Enabled)))
        {
            return services;
        }

        if (IsObjectStorage(section[nameof(GeoprocessingOutputStagingOptions.Provider)]))
        {
            AddObjectStorageOutputStore(services, storageKeyPrefix);
        }
        else
        {
            AddLocalOutputStore(services);
        }

        return services;
    }

    private const string OutputStoreHealthCheckName = "gp-output-store";

    private static void AddObjectStorageOutputStore(
        IServiceCollection services,
        GeoprocessingOutputStorageKeyPrefix storageKeyPrefix)
    {
        services.TryAddSingleton(storageKeyPrefix);
        services.TryAddSingleton<IGeoprocessingOutputObjectStore, ObjectStorageGeoprocessingOutputObjectStore>();
        services.TryAddSingleton<ObjectStorageGeoprocessingOutputStoreHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<ObjectStorageGeoprocessingOutputStoreHealthCheck>(OutputStoreHealthCheckName, tags: ["ready"]);
    }

    private static void AddLocalOutputStore(IServiceCollection services)
    {
        services.TryAddSingleton<IGeoprocessingOutputObjectStore, FileSystemGeoprocessingOutputObjectStore>();
        services.TryAddSingleton<GeoprocessingOutputStoreHealthCheck>();
        services.AddHostedService<GeoprocessingOutputStoreAttestationReporter>();
        services.AddHealthChecks().AddCheck<GeoprocessingOutputStoreHealthCheck>(OutputStoreHealthCheckName, tags: ["ready"]);
    }

    private static bool IsLocal(GeoprocessingOutputStagingOptions options)
        => string.Equals(options.Provider, GeoprocessingOutputStagingOptions.LocalProvider, StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectStorage(GeoprocessingOutputStagingOptions options)
        => IsObjectStorage(options.Provider);

    private static bool IsObjectStorage(string? provider)
        => string.Equals(provider, GeoprocessingOutputStagingOptions.ObjectStorageProvider, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownProvider(string provider)
        => string.Equals(provider, GeoprocessingOutputStagingOptions.LocalProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, GeoprocessingOutputStagingOptions.ObjectStorageProvider, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderStoragePrefix(string keyPrefix, GeoprocessingOutputStorageKeyPrefix storageKeyPrefix)
        => storageKeyPrefix.Value.Length == 0
            || keyPrefix.Trim('/').StartsWith(storageKeyPrefix.Value + "/", StringComparison.Ordinal);
}
