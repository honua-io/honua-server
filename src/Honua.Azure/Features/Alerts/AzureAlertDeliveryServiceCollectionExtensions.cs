// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventHubs.Producer;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Registers the Azure-backed alert delivery sinks (Event Grid, Event Hub) and
/// their thin publisher adapters. Carved out of Honua.Server per the cloud-SDK
/// isolation contract so that the Azure.* surface is confined to Honua.Azure.
/// Falls back to <see cref="UnsupportedAlertDeliverySink"/> when the corresponding
/// channel is not configured, preserving the Server's pre-split behavior.
/// </summary>
internal static class AzureAlertDeliveryServiceCollectionExtensions
{
    public static IServiceCollection AddAzureAlertDeliveryChannels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterAzureEventGrid(services, configuration);
        RegisterAzureEventHub(services, configuration);

        return services;
    }

    private static void RegisterAzureEventGrid(IServiceCollection services, IConfiguration configuration)
    {
        var egSection = configuration.GetSection($"{AlertDeliveryOptions.SectionName}:Dispatch:AzureEventGrid");
        var topicEndpoint = egSection.GetValue<string>("TopicEndpoint");

        if (!string.IsNullOrWhiteSpace(topicEndpoint))
        {
            var topicKey = egSection.GetValue<string>("TopicKey");
            services.AddSingleton(_ =>
            {
                var endpoint = new Uri(topicEndpoint);
                return string.IsNullOrWhiteSpace(topicKey)
                    ? new EventGridPublisherClient(endpoint, new DefaultAzureCredential())
                    : new EventGridPublisherClient(endpoint, new AzureKeyCredential(topicKey));
            });
            services.AddSingleton<IAlertDeliverySink, AzureEventGridAlertDeliverySink>();
        }
        else
        {
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.AzureEventGrid));
        }
    }

    private static void RegisterAzureEventHub(IServiceCollection services, IConfiguration configuration)
    {
        var ehSection = configuration.GetSection($"{AlertDeliveryOptions.SectionName}:Dispatch:AzureEventHub");
        var connectionString = ehSection.GetValue<string>("ConnectionString");
        var eventHubName = ehSection.GetValue<string>("EventHubName");

        if (!string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(eventHubName))
        {
            services.AddSingleton<IEventHubPublisher>(_ =>
                new EventHubPublisher(new EventHubProducerClient(connectionString, eventHubName)));
            services.AddSingleton<IAlertDeliverySink, AzureEventHubAlertDeliverySink>();
        }
        else
        {
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.AzureEventHub));
        }
    }
}
