// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.FileStorage;
using Honua.Infrastructure.SharedCaching;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.PairedPublications;

internal static class PublicPublicationDestinationServiceCollectionExtensions
{
    internal static IServiceCollection AddPublicPublicationDestination(this IServiceCollection services, IConfiguration configuration)
    {
        var publicData = new PublicDataStorageOptions();
        configuration.GetSection("FileStorage:PublicData").Bind(publicData);
        if (!publicData.Enabled)
        {
            services.AddSingleton(_ => new PublicExportPromoter(deps: null, destination: null));
            return services;
        }

        RegisterDestination(services, configuration, publicData);
        RegisterCoreServices(services, configuration);
        services.AddScoped<IPublicExportAccessChangeListener>(provider =>
            provider.GetRequiredService<PublicExportRevocationService>());
        services.AddHostedService<PublicExportRevocationHostedService>();
        return services;
    }

    private static void RegisterCoreServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(HttpPublicExportCanary.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<PublicExportEligibility>();
        services.AddSingleton<IPublicExportStateStore, CloudStoragePublicExportStateStore>();
        services.AddSingleton<IPublicExportCanary, HttpPublicExportCanary>();
        services.AddSingleton<IPublicExportOpsNotifier, OpsNotificationPublicExportNotifier>();
        services.AddScoped(provider => new PublicExportPromoterDependencies
        {
            PrivateStorage = provider.GetRequiredService<ICloudFileStorage>(),
            Eligibility = provider.GetRequiredService<PublicExportEligibility>(),
            StateStore = provider.GetRequiredService<IPublicExportStateStore>(),
            Canary = provider.GetRequiredService<IPublicExportCanary>(),
            OpsNotifier = provider.GetRequiredService<IPublicExportOpsNotifier>()
        });
        services.AddScoped<PublicExportPromoter>();
        services.AddScoped(provider => new PublicExportRevocationDependencies
        {
            StateStore = provider.GetRequiredService<IPublicExportStateStore>(),
            Eligibility = provider.GetRequiredService<PublicExportEligibility>(),
            GraphProvider = provider.GetRequiredService<IMetadataV2GraphProvider>(),
            CdnInvalidation = provider.GetRequiredService<CdnInvalidationService>(),
            OpsNotifier = provider.GetRequiredService<IPublicExportOpsNotifier>(),
            Configuration = configuration,
            Destination = provider.GetService<IPublicPublicationDestination>(),
            StatusReader = provider.GetService<ICdnInvalidationStatusReader>(),
            HttpContextAccessor = provider.GetService<IHttpContextAccessor>()
        });
        services.AddScoped<PublicExportRevocationService>();
    }

    private static void RegisterDestination(
        IServiceCollection services,
        IConfiguration configuration,
        PublicDataStorageOptions publicData)
    {
#if HONUA_EXCLUDE_AWS
        throw new InvalidOperationException("Public data storage requires a server build with AWS support.");
#else
        if (string.IsNullOrWhiteSpace(publicData.BucketName) || string.IsNullOrWhiteSpace(publicData.Region) ||
            string.Equals(publicData.BucketName, configuration["FileStorage:AwsS3:BucketName"], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Public data requires a region and a bucket distinct from private file storage.");
        }

        if (!Uri.TryCreate(publicData.BaseUrl, UriKind.Absolute, out var origin))
        {
            throw new InvalidOperationException("Public data requires an absolute HTTPS BaseUrl.");
        }

        PublicPublicationDestination.ValidateOrigin(origin);

        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options { BucketName = publicData.BucketName, Region = publicData.Region, EnableServerSideEncryption = false }
        });
        services.AddSingleton<IPublicPublicationDestination>(provider => PublicPublicationDestination.CreateOwned(
            new AwsS3FileStorage(options, provider.GetRequiredService<ILogger<AwsS3FileStorage>>(),
                provider.GetRequiredService<IUploadProgressStore>()), origin));
#endif
    }
}
