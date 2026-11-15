// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.EmbedGovernance;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Admin.EmbedGovernance;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Infrastructure.Configuration;
using Npgsql;

namespace Honua.Server.Startup;

// Console content, share, open-data and embed-key state. A host with a system database keeps it there, so
// every task serves the same state and it survives a restart (ADR-0086). A host without one runs a single
// task and keeps it in memory; the session bootstrap reports which, and startup refuses more than one task.
internal static class ConsoleStateStoreRegistration
{
    public static IServiceCollection AddConsoleStateStores(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresSharedState(configuration);
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuthoredConsoleContent>(static provider => HasDatabase(provider)
            ? new PostgresConsoleContentStore(Documents(provider), Time(provider), provider.GetService<IHttpContextAccessor>())
            : new InMemoryConsoleContentStore(Time(provider)));
        services.AddSingleton<IConsoleContentStore>(static provider => provider.GetRequiredService<IAuthoredConsoleContent>());
        services.AddSingleton<IConsoleShareStore>(static provider => HasDatabase(provider)
            ? new PostgresConsoleShareStore(
                Documents(provider), provider.GetRequiredService<ISecurityTokenStore>(), Time(provider),
                provider.GetService<IHttpContextAccessor>())
            : new InMemoryConsoleShareStore(Time(provider)));
        services.AddSingleton<IConsoleOpenDataStore>(static provider => HasDatabase(provider)
            ? new PostgresConsoleOpenDataStore(Documents(provider), Time(provider), provider.GetService<IHttpContextAccessor>())
            : new InMemoryConsoleOpenDataStore(Time(provider)));
        services.AddSingleton<IEmbedKeyStore>(static provider => HasDatabase(provider)
            ? new PostgresEmbedKeyStore(
                Documents(provider), provider.GetRequiredService<JobStoreDatabase>(), Time(provider),
                provider.GetService<IHttpContextAccessor>(), provider.GetService<ILogger<PostgresEmbedKeyStore>>())
            : new InMemoryEmbedKeyStore(provider.GetService<TimeProvider>()));
        services.AddSingleton(static provider => HasDatabase(provider)
            ? ConsolePersistenceNotice.Postgres
            : ConsolePersistenceNotice.InMemory);
        services.AddHostedService<ConsoleInMemoryStoreStartupGuard>();
        return services;
    }

    private static bool HasDatabase(IServiceProvider provider) => provider.GetService<NpgsqlDataSource>() is not null;

    private static PostgresConfigurationDocuments Documents(IServiceProvider provider)
        => provider.GetRequiredService<PostgresConfigurationDocuments>();

    private static TimeProvider Time(IServiceProvider provider) => provider.GetService<TimeProvider>() ?? TimeProvider.System;
}
