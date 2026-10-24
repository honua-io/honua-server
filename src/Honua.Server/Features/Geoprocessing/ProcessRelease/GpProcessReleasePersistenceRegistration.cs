// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Geoprocessing.ProcessRelease;

namespace Honua.Server.Features.Geoprocessing.ProcessRelease;

internal static class GpProcessReleasePersistenceRegistration
{
    internal static IServiceCollection AddGpProcessReleasePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Match primary provider composition, including aliases and whitespace.
        var provider = DataProviderNames.Normalize(configuration["DataSource:Provider"]);
        if (provider is DataProviderNames.Postgis or DataProviderNames.PostgreSql)
        {
            services.AddSingleton<IGpProcessReleaseStore>(sp =>
                new PostgresGpProcessReleaseStore(
                    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<TimeProvider>(),
                    configuration["Database:Schema"]));
        }

        return services;
    }
}
