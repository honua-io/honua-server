// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.FileStorage;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Service collection extensions for COG feature registration.
/// </summary>
internal static class CogServiceCollectionExtensions
{
    /// <summary>
    /// Registers COG services: range readers, metadata parser, and tile resolver.
    /// Provider-specific range readers (AWS S3 / Azure Blob) are delegated to the
    /// per-cloud extensions in Honua.Aws / Honua.Azure to keep the AWSSDK.* /
    /// Azure.* surface confined to those assemblies.
    /// </summary>
    public static IServiceCollection AddCogServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register the AOT-safe COG metadata reader
        services.AddSingleton<ICogMetadataReader, CogMetadataExtractor>();

        // Register the tile resolver
        services.AddScoped<ICogTileResolver, CogTileResolver>();

        // Register range readers based on available provider configurations.
#if !HONUA_EXCLUDE_AWS
        services.AddAwsCloudRangeReader(configuration);
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddAzureCloudRangeReader(configuration);
#endif

        return services;
    }
}
