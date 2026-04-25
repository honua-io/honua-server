// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.SpatialAnalytics;

/// <summary>
/// Service collection extensions for the Pro-tier spatial analytics slice.
/// Registration of <see cref="Core.Features.SpatialAnalytics.Abstractions.ISpatialAnalyticsReader"/>
/// happens in the Postgres project's <c>ServiceCollectionExtensions.AddPostgresFeatureStore</c>
/// alongside the other feature-store readers. This method exists so the
/// composition root registers analytics alongside the rest of the feature slice
/// even when no additional server-side services are required.
/// </summary>
internal static class SpatialAnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddSpatialAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Reader is registered in Honua.Postgres.Features.FeatureStore.ServiceCollectionExtensions.
        // This hook is kept so AddServerFeatures can include analytics alongside the rest of
        // the server feature slices and so future server-side additions (metrics, caches) have
        // a single place to land without touching the composition root.
        return services;
    }
}
