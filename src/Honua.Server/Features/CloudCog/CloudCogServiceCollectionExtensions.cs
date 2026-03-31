// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.S3;
using Azure.Storage.Blobs;
using Google.Cloud.Storage.V1;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Server.Features.FileStorage;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.CloudCog;

/// <summary>
/// Service collection extensions for Cloud COG feature registration.
/// </summary>
internal static class CloudCogServiceCollectionExtensions
{
    /// <summary>
    /// Registers cloud COG services: range readers, metadata parser, and tile resolver.
    /// </summary>
    public static IServiceCollection AddCloudCogServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register the AOT-safe COG metadata reader
        services.AddSingleton<ICogMetadataReader, CogMetadataExtractor>();

        // Register the tile resolver
        services.AddScoped<ICloudCogTileResolver, CloudCogTileResolver>();

        // Register range readers based on available provider configurations
        var fileStorageSection = configuration.GetSection("FileStorage");
        var providerName = fileStorageSection.GetValue<string>("Provider") ?? "Local";

        // Always register S3 range reader if S3 configuration is present
        if (fileStorageSection.GetSection("AwsS3").Exists())
        {
            services.AddSingleton<ICloudRangeReader>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<CloudStorageOptions>>();
                var s3Options = options.Value.AwsS3;
                if (s3Options == null)
                {
                    throw new InvalidOperationException("AWS S3 options not configured for range reader.");
                }

                var config = new AmazonS3Config
                {
                    RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s3Options.Region),
                    ForcePathStyle = s3Options.ForcePathStyle
                };
                if (!string.IsNullOrWhiteSpace(s3Options.ServiceUrl))
                {
                    config.ServiceURL = s3Options.ServiceUrl;
                }

                IAmazonS3 client;
                if (!string.IsNullOrWhiteSpace(s3Options.AccessKeyId) && !string.IsNullOrWhiteSpace(s3Options.SecretAccessKey))
                {
                    client = new AmazonS3Client(
                        new Amazon.Runtime.BasicAWSCredentials(s3Options.AccessKeyId, s3Options.SecretAccessKey),
                        config);
                }
                else
                {
                    client = new AmazonS3Client(config);
                }

                return new AwsS3RangeReader(client);
            });
        }

        // Always register Azure Blob range reader if Azure configuration is present
        if (fileStorageSection.GetSection("AzureBlob").Exists())
        {
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
        }

        // Register GCS range reader if GCS configuration is present
        if (fileStorageSection.GetSection("GoogleCloudStorage").Exists())
        {
            services.AddSingleton<ICloudRangeReader>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<CloudStorageOptions>>();
                var gcsOptions = options.Value.GoogleCloudStorage;
                if (gcsOptions == null)
                {
                    throw new InvalidOperationException("Google Cloud Storage options not configured for range reader.");
                }

                StorageClient client;
                if (!string.IsNullOrWhiteSpace(gcsOptions.CredentialPath))
                {
                    var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromFile(gcsOptions.CredentialPath);
                    client = StorageClient.Create(credential);
                }
                else
                {
                    client = StorageClient.Create();
                }

                return new GcsRangeReader(client);
            });
        }

        return services;
    }
}
