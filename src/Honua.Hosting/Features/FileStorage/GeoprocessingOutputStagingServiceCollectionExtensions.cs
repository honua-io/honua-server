// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.FileStorage;

public static class GeoprocessingOutputStagingServiceCollectionExtensions
{
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
            .Validate(options => !options.Enabled || GeoprocessingOutputStoreAttestationValidator.IsValid(options),
                GeoprocessingOutputStoreAttestationValidator.FailureMessage)
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
            .Validate(
                options => options.ReadLeaseDuration > TimeSpan.Zero
                    && options.ReadLeaseDuration <= TimeSpan.FromDays(1),
                "Geoprocessing:OutputStaging:ReadLeaseDuration must be greater than zero and no more than one day.")
            .ValidateOnStart();

        if (section.GetValue<bool>(nameof(GeoprocessingOutputStagingOptions.Enabled)))
        {
            services.TryAddSingleton<IGeoprocessingOutputObjectStore, FileSystemGeoprocessingOutputObjectStore>();
            services.TryAddSingleton<GeoprocessingOutputStoreHealthCheck>();
            services.AddHostedService<GeoprocessingOutputStoreAttestationReporter>();
            services.AddHealthChecks().AddCheck<GeoprocessingOutputStoreHealthCheck>("gp-output-store", tags: ["ready"]);
        }

        return services;
    }
}
