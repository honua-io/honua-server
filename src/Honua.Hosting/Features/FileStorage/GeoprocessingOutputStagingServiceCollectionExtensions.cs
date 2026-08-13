// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.FileStorage;

/// <summary>
/// Registers the opt-in geoprocessing output staging store (#3089). Called by both the
/// serving host and the GDAL worker host so staged references written by one side
/// resolve on the other. When staging is disabled no store is registered and callers
/// keep the legacy bounded inline publication path.
/// </summary>
public static class GeoprocessingOutputStagingServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="GeoprocessingOutputStagingOptions"/> and registers the
    /// configured <see cref="IGeoprocessingOutputObjectStore"/>. An enabled but
    /// unimplemented provider fails closed at startup with an actionable error rather
    /// than silently falling back to inline publication.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Host configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddGeoprocessingOutputStaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GeoprocessingOutputStagingOptions.SectionName);
        services
            .AddOptions<GeoprocessingOutputStagingOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => !options.Enabled
                    || !string.Equals(
                        options.Provider, GeoprocessingOutputStagingOptions.LocalProvider, StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(options.LocalRootPath),
                "Geoprocessing:OutputStaging:LocalRootPath is required when the local staging provider is enabled.")
            .Validate(
                options => !options.Enabled
                    || string.Equals(
                        options.Provider, GeoprocessingOutputStagingOptions.LocalProvider, StringComparison.OrdinalIgnoreCase),
                "Geoprocessing:OutputStaging:Provider must be 'local'; remote staging providers for AWS Batch "
                    + "placement are configured on the worker image and are not implemented by this host yet (#3089).")
            .Validate(
                options => options.SweepInterval > TimeSpan.Zero
                    && options.SweepInterval <= TimeSpan.FromDays(1),
                "Geoprocessing:OutputStaging:SweepInterval must be greater than zero and no more than one day.")
            .ValidateOnStart();

        if (section.GetValue<bool>(nameof(GeoprocessingOutputStagingOptions.Enabled)))
        {
            services.TryAddSingleton<IGeoprocessingOutputObjectStore, FileSystemGeoprocessingOutputObjectStore>();
        }

        return services;
    }
}
