// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Metadata;

/// <summary>
/// Service collection extensions for registering Metadata v2 services.
/// </summary>
public static class MetadataServiceCollectionExtensions
{
    /// <summary>
    /// Registers Metadata v2 release package services.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    public static IServiceCollection AddMetadataReleaseServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IMetadataV2EnvironmentSnapshotReader, MetadataV2GraphProviderEnvironmentSnapshotReader>();
        services.TryAddSingleton<IMetadataReleasePackageStore, InMemoryMetadataReleasePackageStore>();
        services.TryAddScoped<IMetadataReleaseService, MetadataReleaseService>();
        return services;
    }

    /// <summary>
    /// Registers the file-backed Metadata v2 graph provider. Loads a single JSON document
    /// at the given path. Intended for tests, fixtures, and dev scenarios where Postgres
    /// is not available; production should use the Postgres-backed store.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="graphPath">Path to the Metadata v2 graph JSON document.</param>
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
