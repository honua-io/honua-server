// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

            return new AwsS3RangeReader(AwsS3FileStorage.CreateClient(s3Options));
        });

        return services;
    }
}
