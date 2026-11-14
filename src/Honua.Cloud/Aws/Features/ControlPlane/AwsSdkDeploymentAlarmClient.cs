// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Honua.Cloud.Aws.Features;

namespace Honua.ControlPlane;

internal sealed class AwsSdkDeploymentAlarmClient : IAwsDeploymentAlarmClient
{
    public async Task<AwsDeploymentAlarmSnapshot> ReadAsync(
        string region, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var config = new AmazonCloudWatchConfig { Timeout = TimeSpan.FromSeconds(3), MaxErrorRetry = 0 };
        AwsClientEndpoint.Apply(config, region, null);
        using var client = new AmazonCloudWatchClient(config);
        var response = await client.DescribeAlarmsAsync(new DescribeAlarmsRequest
        {
            AlarmNames = names.ToList(),
            AlarmTypes = [AlarmType.MetricAlarm, AlarmType.CompositeAlarm]
        }, cancellationToken).ConfigureAwait(false);

        return new AwsDeploymentAlarmSnapshot
        {
            IsComplete = response.HttpStatusCode == HttpStatusCode.OK && string.IsNullOrEmpty(response.NextToken),
            Alarms = (response.MetricAlarms ?? []).Select(alarm =>
                    new AwsDeploymentAlarmState(alarm.AlarmName, alarm.StateValue?.Value ?? string.Empty))
                .Concat((response.CompositeAlarms ?? []).Select(alarm =>
                    new AwsDeploymentAlarmState(alarm.AlarmName, alarm.StateValue?.Value ?? string.Empty)))
                .ToArray()
        };
    }
}
