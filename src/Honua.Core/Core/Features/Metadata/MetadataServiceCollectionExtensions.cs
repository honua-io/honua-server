// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Metadata;

public static class MetadataServiceCollectionExtensions
{
    public static IServiceCollection AddMetadataReleaseServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IMetadataV2EnvironmentSnapshotReader, MetadataV2GraphProviderEnvironmentSnapshotReader>();
        services.TryAddSingleton<IMetadataReleasePackageStore, InMemoryMetadataReleasePackageStore>();
        services.TryAddScoped<IMetadataReleaseService, MetadataReleaseService>();
        services.TryAddScoped<IMetadataCompatibilityPrevalidationService, MetadataCompatibilityPrevalidationService>();
        return services;
    }

    public static IServiceCollection AddMetadataV2GraphSnapshotCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MetadataV2GraphSnapshotCache>();
        services.TryAddSingleton<IMetadataV2GraphCacheInvalidator>(
            static sp => sp.GetRequiredService<MetadataV2GraphSnapshotCache>());
        return services;
    }

    public static IServiceCollection AddFileMetadataV2Graph(this IServiceCollection services, string graphPath)
    {
        ArgumentNullException.ThrowIfNull(graphPath);
        services.AddSingleton<IMetadataV2GraphProvider>(sp =>
            new FileMetadataV2GraphProvider(
                graphPath,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileMetadataV2GraphProvider>>()));
        return services;
    }
}
