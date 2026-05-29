// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Registers the AWS-backed alert delivery sinks (SNS, SQS) and their thin
/// publisher adapters. Carved out of Honua.Server per the cloud-SDK isolation
/// contract so that the AWSSDK.* surface is confined to Honua.Aws.
/// Falls back to <see cref="UnsupportedAlertDeliverySink"/> when the corresponding
/// channel is not configured, preserving the Server's pre-split behavior.
/// </summary>
internal static class AwsAlertDeliveryServiceCollectionExtensions
{
    public static IServiceCollection AddAwsAlertDeliveryChannels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterAwsSns(services, configuration);
        RegisterAwsSqs(services, configuration);

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
}
