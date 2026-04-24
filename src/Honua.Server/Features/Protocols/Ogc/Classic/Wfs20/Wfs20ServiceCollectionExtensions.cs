// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Edit;
using Honua.Core.Features.Query;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Service collection extensions for WFS 2.0
/// </summary>
internal static class Wfs20ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WFS 2.0 services
    /// </summary>
    internal static IServiceCollection AddWfs20(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<Wfs20Options>(
            configuration.GetSection(Wfs20Options.SectionName));

        // Register WFS 2.0 core services following established patterns
        services.TryAddScoped<IQueryProcessor, QueryProcessor>();
        services.TryAddScoped<IEditProcessor, EditProcessor>();
        services.TryAddScoped<IQueryParameterAdapter<Wfs20QueryRequest>, Wfs20QueryParameterAdapter>();
        services.TryAddScoped<IEditParameterAdapter<Wfs20EditRequest>, Wfs20EditParameterAdapter>();
        services.AddScoped<Wfs20QueryServices>();
        services.AddScoped<Wfs20Handler>();

        // Register additional WFS 2.0 services for comprehensive OGC compliance
        services.AddScoped<IWfs20FeatureTypeSchemaGenerator, Wfs20FeatureTypeSchemaGenerator>();
        services.AddScoped<IGmlSerializer, GmlSerializer>();
        services.AddScoped<IWfs20FeatureFormatConverter, Wfs20FeatureFormatConverter>();

        return services;
    }
}
