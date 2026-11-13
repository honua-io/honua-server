// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Features.Realtime;

// The realtime event log and the fan-out provider (ADR-0085). This is the only place that names a
// concrete provider; everything else depends on IRealtimeFanoutProvider. Safe to call more than once.
internal static class RealtimeServiceCollectionExtensions
{
    private const string DeploymentModeKey = "Deployment:Mode";
    private const string MultiNodeMode = "MultiNode";

    // The log, its options, its retention sweep and the startup write of per-family retention.
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

    // The fan-out provider chosen by Realtime:Provider, and on hosts that run database polling
    // loops the service that follows the log (with the listener for the PostgreSQL provider). A
    // frozen serverless handler must hold no listener and follow no log.
    public static IServiceCollection AddRealtimeFanout(
        this IServiceCollection services,
        IConfiguration configuration,
        bool hostsDatabasePollingLoops)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRealtimeEventLog(configuration);
        services.TryAddSingleton<RealtimeDispatcher>();
        services.TryAddSingleton<IRealtimeWakeSignal>(provider => provider.GetRequiredService<RealtimeDispatcher>());
        services.TryAddSingleton<RealtimePresenceBatcher>();

        var crossNode = UsesPostgresProvider(configuration);
        services.TryAddSingleton<IRealtimeFanoutProvider>(provider =>
        {
            var log = provider.GetRequiredService<PostgresRealtimeEventLog>();
            var dispatcher = provider.GetRequiredService<RealtimeDispatcher>();
            var presence = provider.GetRequiredService<RealtimePresenceBatcher>();
            return crossNode
                ? new PostgresRealtimeFanoutProvider(log, dispatcher, presence)
                : new InProcessRealtimeFanoutProvider(log, dispatcher, presence);
        });

        if (crossNode)
        {
            services.TryAddSingleton(provider => RealtimeListener.Create(
                configuration.GetConnectionString(RealtimeOptions.ListenerConnectionStringName),
                provider.GetService<NpgsqlDataSource>()?.ConnectionString,
                provider.GetRequiredService<IOptions<RealtimeOptions>>(),
                provider.GetRequiredService<ILogger<RealtimeListener>>()));
        }

        if (hostsDatabasePollingLoops)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RealtimeTailBackgroundService>(
                provider => new RealtimeTailBackgroundService(
                    provider.GetService<NpgsqlDataSource>() is null ? null : provider.GetRequiredService<RealtimeDispatcher>(),
                    provider.GetRequiredService<RealtimePresenceBatcher>(),
                    crossNode ? provider.GetRequiredService<RealtimeListener>() : null,
                    provider.GetRequiredService<IOptions<RealtimeOptions>>(),
                    provider.GetRequiredService<ILogger<RealtimeTailBackgroundService>>())));
        }

        return services;
    }

    // auto follows Deployment:Mode, as the collaboration topology does.
    internal static bool UsesPostgresProvider(IConfiguration configuration)
    {
        var configured = configuration[$"{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.Provider)}"];
        if (string.IsNullOrWhiteSpace(configured) || RealtimeProviderNames.Is(configured, RealtimeProviderNames.Auto))
        {
            return string.Equals(configuration[DeploymentModeKey], MultiNodeMode, StringComparison.OrdinalIgnoreCase);
        }

        if (!RealtimeProviderNames.IsKnown(configured))
        {
            throw new InvalidOperationException($"Realtime:Provider must be auto, in-process or postgres; it is '{configured}'.");
        }

        return RealtimeProviderNames.Is(configured, RealtimeProviderNames.Postgres);
    }
}
