// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
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
        services.TryAddScoped<IMetadataCompatibilityPrevalidationService, MetadataCompatibilityPrevalidationService>();
        return services;
    }

    /// <summary>
    /// Registers the shared, per-instance Metadata v2 graph snapshot cache and its invalidation
    /// hook. Idempotent — safe to call from every provider registration path. The cache lets the
    /// caching provider decorator reuse one materialized snapshot across request scopes instead of
    /// re-reading the full catalog document on every catalog resolution. Staleness is bounded by
    /// <c>Cache:MetadataGraphTtlSeconds</c>; catalog writes invalidate the writing node immediately.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    public static IServiceCollection AddMetadataV2GraphSnapshotCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MetadataV2GraphSnapshotCache>();
        services.TryAddSingleton<IMetadataV2GraphCacheInvalidator>(
            static sp => sp.GetRequiredService<MetadataV2GraphSnapshotCache>());
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
