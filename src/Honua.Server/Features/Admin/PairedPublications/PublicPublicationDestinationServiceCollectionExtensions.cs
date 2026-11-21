// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.PairedPublications;

internal static class PublicPublicationDestinationServiceCollectionExtensions
{
    internal static IServiceCollection AddPublicPublicationDestination(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(HttpPublicExportCanary.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<PublicExportEligibility>();
        services.AddSingleton<IPublicExportStateStore, CloudStoragePublicExportStateStore>();
        services.AddSingleton<IPublicExportCanary, HttpPublicExportCanary>();
        services.AddSingleton<IPublicExportOpsNotifier, OpsNotificationPublicExportNotifier>();
        services.AddScoped<PublicExportPromoter>();
        services.AddScoped<PublicExportRevocationService>();

        var publicData = new PublicDataStorageOptions();
        configuration.GetSection("FileStorage:PublicData").Bind(publicData);
        if (!publicData.Enabled)
        {
            return services;
        }

        services.AddScoped<IPublicExportAccessChangeListener>(provider =>
            provider.GetRequiredService<PublicExportRevocationService>());
        services.AddHostedService<PublicExportRevocationHostedService>();

#if HONUA_EXCLUDE_AWS
        throw new InvalidOperationException("Public data storage requires a server build with AWS support.");
#else
        var bucket = publicData.BucketName;
        var region = publicData.Region;
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(region) ||
            string.Equals(bucket, configuration["FileStorage:AwsS3:BucketName"], StringComparison.OrdinalIgnoreCase))
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
            AwsS3 = new AwsS3Options { BucketName = bucket, Region = region, EnableServerSideEncryption = false }
        });
        services.AddSingleton<IPublicPublicationDestination>(provider => new PublicPublicationDestination(
            new AwsS3FileStorage(options, provider.GetRequiredService<ILogger<AwsS3FileStorage>>(),
                provider.GetRequiredService<IUploadProgressStore>()), origin));
        return services;
#endif
    }
}
