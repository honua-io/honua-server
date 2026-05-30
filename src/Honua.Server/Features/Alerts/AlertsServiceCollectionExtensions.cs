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

        services.AddScoped<IAlertPipeline, AlertPipeline>();
        services.AddScoped<IAlertEvaluator, DefaultAlertEvaluator>();
        services.AddScoped<IAlertEditionPolicy, AlertEditionPolicy>();
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

        services.AddHostedService<AlertEvaluationBackgroundService>();
        services.AddHostedService<AlertDispatchBackgroundService>();

        return services;
    }
}
