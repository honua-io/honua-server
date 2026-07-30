// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Alerts;

/// <summary>
/// Cloud-neutral orchestration over the alert delivery channels: registers the
/// HTTP-based sinks (Webhook, Slack, Teams, Email digest, WebSocket) and then
/// delegates AWS / Azure channel registration to the per-cloud extensions in
/// Honua.Aws / Honua.Azure. Keeping the AWS / Azure SDK surface confined to
/// those assemblies preserves the cloud-SDK isolation contract enforced by
/// CloudSdkIsolationTests.
/// </summary>
internal static class AlertDeliveryServiceCollectionExtensions
{
    public static IServiceCollection AddAlertDeliveryChannels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddResilientHttpClient(
            "alerts-webhook",
            "alerts-webhook",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => Honua.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
        services.AddResilientHttpClient(
            "alerts-digest",
            "alerts-digest",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => Honua.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
        services.AddResilientHttpClient(
            "alerts-slack",
            "alerts-slack",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => Honua.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
        services.AddResilientHttpClient(
            "alerts-teams",
            "alerts-teams",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => Honua.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());

        // Single outbound-destination guard shared by every HTTP alert sink, so the SSRF check and
        // its retryable/non-retryable classification are implemented once (#3057).
        services.TryAddSingleton(_ => new AlertDestinationGuard());

        services.AddSingleton<IAlertDeliverySink, WebhookAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink, WebSocketAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink, EmailAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.Digest));

        // Alert digest flush is a PERIODIC tick (bucket-b). The flush claims a batch atomically and
        // is idempotent, so the scheduled-tick handler is registered in BOTH trigger modes; the
        // in-process timer is hosted only under TriggerMode=Poll (default, on-prem), keeping that
        // path byte-for-byte unchanged.
        services.TryAddSingleton<DigestFlushBackgroundService>();
        services.AddSingleton<IScheduledTickHandler, DigestFlushScheduledTickHandler>();
        if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
        {
            services.AddHostedService(sp => sp.GetRequiredService<DigestFlushBackgroundService>());
        }

#if HONUA_EXCLUDE_AWS
        RegisterUnsupportedAwsChannels(services);
#else
        services.AddAwsAlertDeliveryChannels(configuration);
#endif
#if HONUA_EXCLUDE_AZURE
        RegisterUnsupportedAzureChannels(services);
#else
        services.AddAzureAlertDeliveryChannels(configuration);
#endif
        RegisterSlack(services, configuration);
        RegisterTeams(services, configuration);

        return services;
    }

    private static void RegisterUnsupportedAwsChannels(IServiceCollection services)
    {
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.AwsSns));
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.AwsSqs));
    }

    private static void RegisterUnsupportedAzureChannels(IServiceCollection services)
    {
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.AzureEventGrid));
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.AzureEventHub));
    }

    private static void RegisterSlack(IServiceCollection services, IConfiguration configuration)
    {
        var slackSection = configuration.GetSection($"{AlertDeliveryOptions.SectionName}:Dispatch:Slack");
        var webhookUrl = slackSection.GetValue<string>("WebhookUrl");

        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            services.AddSingleton<IAlertDeliverySink, SlackAlertDeliverySink>();
        }
        else
        {
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.Slack));
        }
    }

    private static void RegisterTeams(IServiceCollection services, IConfiguration configuration)
    {
        var teamsSection = configuration.GetSection($"{AlertDeliveryOptions.SectionName}:Dispatch:Teams");
        var webhookUrl = teamsSection.GetValue<string>("WebhookUrl");

        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            services.AddSingleton<IAlertDeliverySink, TeamsAlertDeliverySink>();
        }
        else
        {
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(Core.Features.Alerts.Domain.AlertChannelType.MicrosoftTeams));
        }
    }
}
