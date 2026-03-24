// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Azure;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventHubs.Producer;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Features.Alerts;

internal static class AlertDeliveryServiceCollectionExtensions
{
    public static IServiceCollection AddAlertDeliveryChannels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpClient("alerts-webhook");
        services.AddHttpClient("alerts-digest");
        services.AddHttpClient("alerts-slack");
        services.AddHttpClient("alerts-teams");

        services.AddSingleton<IAlertDeliverySink, WebhookAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink, WebSocketAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink, EmailAlertDeliverySink>();
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.Digest));
        services.AddHostedService<DigestFlushBackgroundService>();

        RegisterAwsSns(services, configuration);
        RegisterAzureEventGrid(services, configuration);
        RegisterSlack(services, configuration);
        RegisterTeams(services, configuration);
        RegisterAwsSqs(services, configuration);
        RegisterAzureEventHub(services, configuration);

        return services;
    }

    private static void RegisterAwsSns(IServiceCollection services, IConfiguration configuration)
    {
        var snsSection = configuration.GetSection($"{AlertDeliveryOptions.SectionName}:Dispatch:AwsSns");
        var topicArn = snsSection.GetValue<string>("TopicArn");

        if (!string.IsNullOrWhiteSpace(topicArn))
        {
            var region = snsSection.GetValue<string>("Region");
            services.AddSingleton<ISnsPublisher>(_ =>
            {
                var config = new AmazonSimpleNotificationServiceConfig();
                if (!string.IsNullOrWhiteSpace(region))
                {
                    config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
                }

                return new AwsSnsPublisher(new AmazonSimpleNotificationServiceClient(config));
            });
            services.AddSingleton<IAlertDeliverySink, AwsSnsAlertDeliverySink>();
        }
        else
        {
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.AwsSns));
        }
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
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.Slack));
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
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.MicrosoftTeams));
        }
    }

    private static void RegisterAwsSqs(IServiceCollection services, IConfiguration configuration)
    {
        var sqsSection = configuration.GetSection($"{AlertDeliveryOptions.SectionName}:Dispatch:AwsSqs");
        var queueUrl = sqsSection.GetValue<string>("QueueUrl");

        if (!string.IsNullOrWhiteSpace(queueUrl))
        {
            var region = sqsSection.GetValue<string>("Region");
            services.AddSingleton<ISqsPublisher>(_ =>
            {
                var config = new AmazonSQSConfig();
                if (!string.IsNullOrWhiteSpace(region))
                {
                    config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
                }

                return new AwsSqsPublisher(new AmazonSQSClient(config));
            });
            services.AddSingleton<IAlertDeliverySink, AwsSqsAlertDeliverySink>();
        }
        else
        {
            services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.AwsSqs));
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
