// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.FileStorage;

/// <summary>Registers the Azure Blob raster output data plane without exposing SDK types.</summary>
public static class AzureRasterOutputServiceCollectionExtensions
{
    /// <summary>Adds the Azure Blob raster output store as both object and manifest storage.</summary>
    public static IServiceCollection AddAzureRasterOutputStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AzureBlobRasterOutputObjectStore>();
        services.TryAddSingleton<IRasterOutputObjectStore>(provider =>
            provider.GetRequiredService<AzureBlobRasterOutputObjectStore>());
        services.TryAddSingleton<IRasterOutputManifestStore>(provider =>
            provider.GetRequiredService<AzureBlobRasterOutputObjectStore>());
        return services;
    }
}
