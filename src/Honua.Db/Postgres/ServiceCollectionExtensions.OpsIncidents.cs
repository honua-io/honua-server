// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Db.Postgres.Features.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres;

internal static partial class ServiceCollectionExtensions
{
    private static void AddOpsIncidentServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PostgresOpsIncidentEpisodeStore>(serviceProvider =>
            new PostgresOpsIncidentEpisodeStore(
                serviceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));
        services.AddScoped<IOpsIncidentEpisodeStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresOpsIncidentEpisodeStore>());
        services.AddScoped<IOpsIncidentEpisodeResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresOpsIncidentEpisodeStore>());
        services.AddScoped<IOpsExternalEventStore>(serviceProvider =>
            new PostgresOpsExternalEventStore(
                serviceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));
        services.AddScoped<IOpsIncidentObservationStore>(serviceProvider =>
            new PostgresOpsIncidentObservationStore(
                serviceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));
    }
}
