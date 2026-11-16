// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Jobs carry the install's stack tag, when one is configured, so IAM can limit cancel and terminate to its own jobs.
public sealed class AwsBatchStackTagTests
{
    [Fact]
    [Trait("Tier", "Fast")]
    public async Task StartAsync_TagsSubmittedJobWithStackName()
    {
        var client = new StubAwsBatchJobClient();
        var backend = CreateBackend(client, " honua-prod ");

        var result = await backend.StartAsync(AwsBatchComputeBackendTests.CreateJob());

        result.Status.Should().Be(ExecutionJobStatus.Queued);
        client.LastSubmission!.Tags.Should().Equal(
            new Dictionary<string, string> { [AwsBatchExecutionOptions.StackTagKey] = "honua-prod" });
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartAsync_WithoutStackName_SubmitsUntagged(string? stackName)
    {
        var client = new StubAwsBatchJobClient();
        var backend = CreateBackend(client, stackName);

        var result = await backend.StartAsync(AwsBatchComputeBackendTests.CreateJob());

        result.Status.Should().Be(ExecutionJobStatus.Queued);
        client.LastSubmission!.Tags.Should().BeEmpty();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void BuildSubmitJobRequest_WithoutTags_SendsNoTags()
    {
        var request = AwsSdkBatchJobClient.BuildSubmitJobRequest(Submission(new Dictionary<string, string>()));

        request.Tags.Should().BeNullOrEmpty();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void BuildSubmitJobRequest_CarriesTags()
    {
        var request = AwsSdkBatchJobClient.BuildSubmitJobRequest(
            Submission(new Dictionary<string, string> { [AwsBatchExecutionOptions.StackTagKey] = "honua-prod" }));

        request.Tags.Should().Equal(new Dictionary<string, string> { [AwsBatchExecutionOptions.StackTagKey] = "honua-prod" });
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void StackName_FallsBackToHeartbeatStackName()
    {
        var options = new AwsBatchExecutionOptions();

        AwsBatchExecutionOptions.ApplyStackNameFallback(options, Configuration("honua-prod"));

        options.StackName.Should().Be("honua-prod");
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void StackName_ExplicitValueWinsOverHeartbeatStackName()
    {
        var options = new AwsBatchExecutionOptions { StackName = "batch-stack" };

        AwsBatchExecutionOptions.ApplyStackNameFallback(options, Configuration("honua-prod"));

        options.StackName.Should().Be("batch-stack");
    }

    private static AwsBatchJobSubmission Submission(IReadOnlyDictionary<string, string> tags) => new()
    {
        JobName = "honua-op-1",
        JobDefinition = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
        JobQueue = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
        Tags = tags
    };

    private static IConfiguration Configuration(string heartbeatStackName) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:Heartbeat:StackName"] = heartbeatStackName })
            .Build();

    private static AwsBatchComputeBackend CreateBackend(IAwsBatchJobClient client, string? stackName) =>
        new(client,
            Options.Create(new AwsBatchExecutionOptions
            {
                StackName = stackName,
                JobDefinitions =
                [
                    new AwsBatchJobDefinitionContractOptions
                    {
                        JobDefinition = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
                        MaxSupportedContractVersion = 1
                    }
                ]
            }),
            NullLogger<AwsBatchComputeBackend>.Instance);
}
