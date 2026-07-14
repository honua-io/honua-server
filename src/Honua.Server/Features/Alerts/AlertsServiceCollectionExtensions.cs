// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal static class AlertsServiceCollectionExtensions
{
    public static IServiceCollection AddAlerts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<AlertOptions>()
            .Bind(configuration.GetSection(AlertOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AlertOptions>, AlertOptionsValidator>();
        services
            .AddOptions<AlertDeliveryOptions>()
            .Bind(configuration.GetSection(AlertDeliveryOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AlertDeliveryOptions>, AlertDeliveryOptionsValidator>();

        // Alert-pipeline OTel metrics: a singleton that owns its instruments on the shared
        // "Honua" meter (created via IMeterFactory), injected into the writer, dispatcher, and
        // ops-notification composition instead of a process-global static (#2802).
        services.AddSingleton<AlertPipelineMetrics>();
        services.AddScoped<AlertDispatchWriter>();
        services.AddScoped<IAlertPipeline, AlertPipeline>();
        services.AddScoped<IAlertEvaluator, DefaultAlertEvaluator>();
        services.AddScoped<IAlertEditionPolicy, AlertEditionPolicy>();

        // Per-channel outbound notification rate cap (shared by the dispatcher across passes).
        services.AddSingleton<AlertNotificationRateLimiter>();

        // Per-channel delivery circuit breaker (shared by the dispatcher and ops-notification
        // composition) so a persistently-failing channel produces bounded dead-letter volume.
        services.AddSingleton<AlertChannelCircuitBreaker>();

        // Second outbox consumer: ops notifications (deploy/job terminal events).
        services.AddScoped<Honua.Alerts.Ops.OpsNotificationService>();
        services.AddSingleton<ILeaderElectionStrategy>(serviceProvider =>
        {
            var dataSource = serviceProvider.GetService<Npgsql.NpgsqlDataSource>();
            return dataSource is null
                ? new SingleInstanceLeaderElectionStrategy()
                : ActivatorUtilities.CreateInstance<PostgresAdvisoryLockLeaderElectionStrategy>(serviceProvider, dataSource);
        });
        services.AddSingleton<InMemoryAlertNotificationBroadcaster>();
        services.AddSingleton<IAlertNotificationBroadcaster>(sp => sp.GetRequiredService<InMemoryAlertNotificationBroadcaster>());
        services.AddSingleton<IStreamingSubscriptionManager>(sp => sp.GetRequiredService<InMemoryAlertNotificationBroadcaster>());
        services.AddAlertDeliveryChannels(configuration);

        // Register the evaluator once and expose it both as the hosted service and as the
        // evaluation health-state source (heartbeat / no-leader), so the evaluation health check
        // and the readiness path read the live snapshot.
        services.AddSingleton<AlertEvaluationBackgroundService>();
        services.AddSingleton<IAlertEvaluationHealth>(sp => sp.GetRequiredService<AlertEvaluationBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlertEvaluationBackgroundService>());

        // Register the dispatcher once and expose it both as the hosted service and as the
        // health-state source, so the dispatch-backlog health check reads the live snapshot.
        services.AddSingleton<AlertDispatchBackgroundService>();
        services.AddSingleton<IAlertDispatchHealth>(sp => sp.GetRequiredService<AlertDispatchBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlertDispatchBackgroundService>());

        return services;
    }
}
