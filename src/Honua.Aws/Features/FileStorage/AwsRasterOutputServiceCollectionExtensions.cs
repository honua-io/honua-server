// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.FileStorage;

/// <summary>Registers the S3 raster output data plane without exposing AWS SDK types.</summary>
public static class AwsRasterOutputServiceCollectionExtensions
{
    /// <summary>Adds the S3 raster output store as both object and manifest storage.</summary>
    public static IServiceCollection AddAwsRasterOutputStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AwsS3RasterOutputObjectStore>();
        services.TryAddSingleton<IRasterOutputObjectStore>(provider =>
            provider.GetRequiredService<AwsS3RasterOutputObjectStore>());
        services.TryAddSingleton<IRasterOutputManifestStore>(provider =>
            provider.GetRequiredService<AwsS3RasterOutputObjectStore>());
        return services;
    }
}
