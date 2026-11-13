// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Features.Realtime;

// The realtime event log (ADR-0085): its options, the store, its retention sweep and the startup
// write of per-family retention. Safe to call more than once.
internal static class RealtimeServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeEventLog(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RealtimeOptions>().Bind(configuration.GetSection(RealtimeOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RealtimeOptions>, RealtimeOptionsValidator>());
        services.TryAddSingleton(provider => new PostgresRealtimeEventLog(provider.GetRequiredService<JobStoreDatabase>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExpiredRowSweep, PostgresRealtimeEventLog>(
            provider => provider.GetRequiredService<PostgresRealtimeEventLog>()));
        if (!ControlPlaneLambdaConnectionRelease.IsEventHandlerProfile(configuration))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RealtimeRetentionStartupService>(
                provider => new RealtimeRetentionStartupService(
                    provider.GetService<NpgsqlDataSource>() is null ? null : provider.GetRequiredService<PostgresRealtimeEventLog>(),
                    provider.GetRequiredService<IOptions<RealtimeOptions>>(),
                    provider.GetRequiredService<ILogger<RealtimeRetentionStartupService>>())));
        }
        return services;
    }
}
