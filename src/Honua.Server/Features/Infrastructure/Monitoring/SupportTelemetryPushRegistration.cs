// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal static class SupportTelemetryPushRegistration
{
    internal static IServiceCollection AddSupportTelemetryPush(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SupportTelemetryPushOptions.SectionName);
        services.Configure<SupportTelemetryPushOptions>(section);

        var bound = new SupportTelemetryPushOptions();
        section.Bind(bound);

        if (!bound.Enabled)
        {
            return services;
        }

        SupportTelemetryPushOptions.ValidateEnabled(bound);

        services.AddHttpClient(SupportTelemetryPushClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            });
        services.AddSingleton<SupportTelemetrySnapshotSources>();
        services.AddSingleton<SupportTelemetrySnapshotCollector>();
        services.AddSingleton<SupportTelemetryTransport>();
        services.AddSingleton<SupportTelemetryPushClient>();

        // Mirror OpsHeartbeatRegistration: do not spawn background polling loops on event-handler Lambdas.
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService<SupportTelemetryPushBackgroundService>();
        }

        return services;
    }
}
