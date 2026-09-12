// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Infrastructure.Analytics;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Reader is registered in Honua.Db.Postgres.Features.FeatureStore.ServiceCollectionExtensions.
        // This hook is kept so AddServerFeatures can include analytics alongside the rest of
        // the server feature slices and so future server-side additions (metrics, caches) have
        // a single place to land without touching the composition root.

        // The canonical analytics selection translation, shared with the source.honua-layer
        // geoprocessing connector so layer-sourced jobs honor geometry/time selectors exactly
        // as the synchronous analytics endpoints do (#4624).
        services.TryAddScoped<SpatialReferenceResolver>();
        services.TryAddScoped<ILayerSelectionFilterTranslator, AnalyticsLayerSelectionFilterTranslator>();
        return services;
    }
}
