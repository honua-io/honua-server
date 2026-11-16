// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Features.Protocols.Wfs20;

internal static class WfsStoredQueryStoreRegistration
{
    // Stored queries are shared through the system database; a host without one runs one node and
    // keeps them in process.
    public static IServiceCollection AddWfsStoredQueryStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Keep database dependencies lazy so database-free hosts validate and use the fallback.
        services.TryAddSingleton(provider => new PostgresWfsStoredQueryStore(
            provider.GetRequiredService<JobStoreDatabase>()));
        services.TryAddSingleton<InMemoryWfsStoredQueryStore>();
        services.RemoveAll<IWfsStoredQueryStore>();
        services.AddSingleton<IWfsStoredQueryStore>(provider => provider.GetService<NpgsqlDataSource>() is not null
            ? provider.GetRequiredService<PostgresWfsStoredQueryStore>()
            : provider.GetRequiredService<InMemoryWfsStoredQueryStore>());
        return services;
    }
}
