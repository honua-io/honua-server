// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>
/// Registers the AWS S3 byte-range reader (<see cref="AwsS3RangeReader"/>) used by
/// the COG / PMTiles range-proxy pipelines. Carved out of Honua.Server's
/// CogServiceCollectionExtensions so the AWSSDK.S3 surface stays confined to
/// Honua.Aws per the cloud-SDK isolation contract.
/// </summary>
internal static class AwsCloudRangeReaderServiceCollectionExtensions
{
    public static IServiceCollection AddAwsCloudRangeReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Always advertise the AWS not-found classifier when the AWSSDK.S3
        // surface is available — cloud-neutral orchestrators (PMTiles proxy)
        // chain every registered classifier without taking an SDK dependency.
        services.AddSingleton<ICloudNotFoundClassifier, AwsS3NotFoundClassifier>();

        var fileStorageSection = configuration.GetSection("FileStorage");
        if (!fileStorageSection.GetSection("AwsS3").Exists())
        {
            return services;
        }

        services.AddSingleton<ICloudRangeReader>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CloudStorageOptions>>();
            var s3Options = options.Value.AwsS3
                ?? throw new InvalidOperationException("AWS S3 options not configured for range reader.");

            var config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(s3Options.Region),
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
                    new BasicAWSCredentials(s3Options.AccessKeyId, s3Options.SecretAccessKey),
                    config);
            }
            else
            {
                client = new AmazonS3Client(config);
            }

            return new AwsS3RangeReader(client);
        });

        return services;
    }
}
