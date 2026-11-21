// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsBatchServiceUrlPolicyTests
{
    [UnitTheory]
    [InlineData("https://batch.us-west-2.amazonaws.com")]
    [InlineData("https://fips.batch.us-gov-west-1.amazonaws.com")]
    [InlineData("https://batch.cn-north-1.amazonaws.com.cn")]
    [InlineData("https://vpce-0abc-1234.batch.us-east-1.vpce.amazonaws.com")]
    [InlineData("http://localhost:4566")]
    [InlineData("http://127.0.0.1:4566")]
    public void CreateClient_AcceptsAwsPartitionHostsAndLoopback(string serviceUrl)
    {
        using var client = AwsSdkBatchJobClient.CreateClient("us-west-2", serviceUrl);

        client.Config.ServiceURL.Should().StartWith(serviceUrl);
    }

    [UnitTheory]
    [InlineData("http://batch.us-west-2.amazonaws.com")]
    [InlineData("https://batch.us-west-2.amazonaws.com.attacker.example")]
    [InlineData("https://attacker.example")]
    [InlineData("http://localstack:4566")]
    [InlineData("https://abc123.execute-api.us-east-1.amazonaws.com")]
    [InlineData("https://bucket.s3-website-us-east-1.amazonaws.com")]
    public void CreateClient_RejectsPlainHttpAndForeignHosts(string serviceUrl)
    {
        var act = () => AwsSdkBatchJobClient.CreateClient("us-west-2", serviceUrl);

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("AWS Batch service URL", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task StartAsync_WithForeignServiceUrl_RefusesBeforeCallingProvider()
    {
        var client = new StubAwsBatchJobClient();
        var backend = new AwsBatchComputeBackend(
            client,
            Options.Create(new AwsBatchExecutionOptions()),
            NullLogger<AwsBatchComputeBackend>.Instance);
        var job = AwsBatchComputeBackendTests.CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            [AwsBatchParameterKeys.ServiceUrl] = "https://attacker.example",
        });

        var act = () => backend.StartAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains(AwsBatchParameterKeys.ServiceUrl, StringComparison.Ordinal));
        client.LastSubmission.Should().BeNull();
    }

    [UnitTest]
    public async Task ObserveAndCancel_WithRefusedServiceUrl_FinalizeWithoutCallingProvider()
    {
        var client = new StubAwsBatchJobClient();
        var backend = new AwsBatchComputeBackend(
            client,
            Options.Create(new AwsBatchExecutionOptions()),
            NullLogger<AwsBatchComputeBackend>.Instance);
        var job = AwsBatchComputeBackendTests.CreateJob(
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AwsBatchParameterKeys.ServiceUrl] = "http://localstack:4566",
            },
            providerOperationId: "job-123",
            status: ExecutionJobStatus.Running);

        var observed = await backend.ObserveAsync(job);
        var cancelled = await backend.CancelAsync(job);

        observed.Status.Should().Be(ExecutionJobStatus.Failed);
        observed.Message.Should().Contain(AwsBatchParameterKeys.ServiceUrl);
        cancelled.Status.Should().Be(ExecutionJobStatus.Cancelled);
        client.DescribeCallCount.Should().Be(0);
        client.CancelCallCount.Should().Be(0);
        client.TerminateCallCount.Should().Be(0);
    }
}
