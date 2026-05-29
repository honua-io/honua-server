// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Storage.Blobs;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Registers the Azure Blob byte-range reader (<see cref="AzureBlobRangeReader"/>)
/// used by the COG / PMTiles range-proxy pipelines. Carved out of Honua.Server's
/// CogServiceCollectionExtensions so the Azure.Storage.Blobs surface stays
/// confined to Honua.Azure per the cloud-SDK isolation contract.
/// </summary>
internal static class AzureCloudRangeReaderServiceCollectionExtensions
{
    public static IServiceCollection AddAzureCloudRangeReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Always advertise the Azure not-found classifier when the Azure.Storage
        // surface is available — cloud-neutral orchestrators (PMTiles proxy)
        // chain every registered classifier without taking an SDK dependency.
        services.AddSingleton<ICloudNotFoundClassifier, AzureBlobNotFoundClassifier>();

        var fileStorageSection = configuration.GetSection("FileStorage");
        if (!fileStorageSection.GetSection("AzureBlob").Exists())
        {
            return services;
        }

        services.AddSingleton<ICloudRangeReader>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CloudStorageOptions>>();
            var azureOptions = options.Value.AzureBlob;
            if (azureOptions == null || string.IsNullOrWhiteSpace(azureOptions.ConnectionString))
            {
                throw new InvalidOperationException("Azure Blob options not configured for range reader.");
            }

            var serviceClient = new BlobServiceClient(azureOptions.ConnectionString);
            return new AzureBlobRangeReader(serviceClient);
        });

        return services;
    }
}
