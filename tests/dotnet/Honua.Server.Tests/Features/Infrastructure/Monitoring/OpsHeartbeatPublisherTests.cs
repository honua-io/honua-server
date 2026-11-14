// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using FluentAssertions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsHeartbeatPublisherTests
{
    [Fact]
    [Operation(Operations.TestInfrastructure)]
    public async Task PublishAsync_HealthyService_SendsOnlyTheStackHeartbeatMetric()
    {
        var client = Substitute.For<IAmazonCloudWatch>();
        PutMetricDataRequest? observed = null;
        client.PutMetricDataAsync(Arg.Do<PutMetricDataRequest>(request => observed = request), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PutMetricDataResponse()));
        using var publisher = new AwsOpsHeartbeatPublisher(client);

        await publisher.PublishAsync("test-stack", CancellationToken.None);

        observed.Should().NotBeNull();
        observed!.Namespace.Should().Be("Honua/Operations");
        var metric = observed.MetricData.Should().ContainSingle().Subject;
        metric.MetricName.Should().Be("Heartbeat");
        metric.Value.Should().Be(1);
        var dimension = metric.Dimensions.Should().ContainSingle().Subject;
        dimension.Name.Should().Be("StackName");
        dimension.Value.Should().Be("test-stack");
        await client.Received(1).PutMetricDataAsync(Arg.Any<PutMetricDataRequest>(), Arg.Any<CancellationToken>());
    }
}
#endif
