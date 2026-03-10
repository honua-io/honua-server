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
        services.AddHttpClient("alerts-digest");
        services.AddHttpClient("alerts-slack");
        services.AddHttpClient("alerts-teams");

        // Webhook (always registered)
        services.AddSingleton<IAlertDeliverySink, WebhookAlertDeliverySink>();

        // WebSocket delivery still needs process-external fan-out semantics to be safe.
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.WebSocket));

        // Email (SMTP)
        services.AddSingleton<IAlertDeliverySink, EmailAlertDeliverySink>();

        // Digest delivery needs durable batching before it can acknowledge outbox rows safely.
        services.AddSingleton<IAlertDeliverySink>(_ => new UnsupportedAlertDeliverySink(AlertChannelType.Digest));
        services.AddHostedService<DigestFlushBackgroundService>();

        // AWS SNS
        RegisterAwsSns(services, configuration);

        // Azure Event Grid
        RegisterAzureEventGrid(services, configuration);

        // Slack
        RegisterSlack(services, configuration);

        // Microsoft Teams
        RegisterTeams(services, configuration);

        // AWS SQS
        RegisterAwsSqs(services, configuration);

        // Azure Event Hub
        RegisterAzureEventHub(services, configuration);

        services.AddHostedService<AlertEvaluationBackgroundService>();
        services.AddHostedService<AlertDispatchBackgroundService>();

        return services;
    }

    private static void RegisterAwsSns(IServiceCollection services, IConfiguration configuration)
    {
        var snsSection = configuration.GetSection($"{AlertOptions.SectionName}:Dispatch:AwsSns");
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
        var egSection = configuration.GetSection($"{AlertOptions.SectionName}:Dispatch:AzureEventGrid");
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
        var slackSection = configuration.GetSection($"{AlertOptions.SectionName}:Dispatch:Slack");
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
        var teamsSection = configuration.GetSection($"{AlertOptions.SectionName}:Dispatch:Teams");
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
        var sqsSection = configuration.GetSection($"{AlertOptions.SectionName}:Dispatch:AwsSqs");
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
        var ehSection = configuration.GetSection($"{AlertOptions.SectionName}:Dispatch:AzureEventHub");
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
