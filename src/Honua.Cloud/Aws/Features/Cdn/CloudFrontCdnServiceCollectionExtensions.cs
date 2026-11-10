// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.CloudFront;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Cloud.Aws.Features.Cdn;

public static class CloudFrontCdnServiceCollectionExtensions
{
    public const string DistributionIdSetting = "SharedCache:CloudFront:DistributionId";

    // Credentials come from the default chain, which is the task role on ECS. Without a distribution id the no-op invalidator stays registered.
    public static IServiceCollection AddCloudFrontCdnInvalidation(this IServiceCollection services, IConfiguration configuration)
    {
        var distributionId = configuration[DistributionIdSetting];
        if (string.IsNullOrWhiteSpace(distributionId))
        {
            return services;
        }

        services.AddSingleton<IAmazonCloudFront>(static _ => new AmazonCloudFrontClient());
        services.AddSingleton<ICdnInvalidator>(provider => new CloudFrontCdnInvalidator(
            provider.GetRequiredService<IAmazonCloudFront>(),
            distributionId.Trim(),
            provider.GetRequiredService<ILogger<CloudFrontCdnInvalidator>>()));
        return services;
    }
}
