// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Honua.Core.Features.Observability.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Monitoring;

internal sealed class AwsOpsHeartbeatPublisher(IAmazonCloudWatch client) : IOpsHeartbeatPublisher, IDisposable
{
    public async Task PublishAsync(string stackName, CancellationToken cancellationToken)
    {
        await client.PutMetricDataAsync(new PutMetricDataRequest
        {
            Namespace = "Honua/Operations",
            MetricData = [new MetricDatum
            {
                MetricName = "Heartbeat",
                Dimensions = [new Dimension { Name = "StackName", Value = stackName }],
                Value = 1,
                Unit = StandardUnit.Count,
                Timestamp = DateTime.UtcNow,
            }],
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();

    internal static void Register(IServiceCollection services, string? region)
    {
        services.AddSingleton<IOpsHeartbeatPublisher>(_ =>
        {
            var config = new AmazonCloudWatchConfig { Timeout = TimeSpan.FromSeconds(10), MaxErrorRetry = 0 };
            if (!string.IsNullOrWhiteSpace(region))
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            }

            return new AwsOpsHeartbeatPublisher(new AmazonCloudWatchClient(config));
        });
    }
}
