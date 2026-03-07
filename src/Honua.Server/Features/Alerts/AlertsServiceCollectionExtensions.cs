// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal static class AlertsServiceCollectionExtensions
{
    public static IServiceCollection AddAlerts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<AlertOptions>()
            .Bind(configuration.GetSection(AlertOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AlertOptions>, AlertOptionsValidator>();

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
        services.AddHttpClient("alerts-webhook");

        services.AddSingleton<IAlertDeliverySink, WebhookAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.WebSocket));
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.Email));
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.Digest));

        services.AddHostedService<AlertEvaluationBackgroundService>();
        services.AddHostedService<AlertDispatchBackgroundService>();

        return services;
    }
}
