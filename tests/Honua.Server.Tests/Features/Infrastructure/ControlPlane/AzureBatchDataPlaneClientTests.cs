// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AzureBatchDataPlaneClientTests
{
    [Fact]
    public void BuildCreateJobPayload_ProducesPoolAndTerminationPolicy()
    {
        var submission = new AzureBatchJobSubmission
        {
            AccountUrl = "https://acct.eastus.batch.azure.com",
            JobId = "honua-job-1",
            PoolId = "gdal-heavy-pool",
            CommandLine = "/bin/bash -c run.sh"
        };

        var bytes = AzureBatchDataPlaneClient.BuildCreateJobPayload(submission);
        using var doc = JsonDocument.Parse(bytes);

        doc.RootElement.GetProperty("id").GetString().Should().Be("honua-job-1");
        doc.RootElement.GetProperty("onAllTasksComplete").GetString().Should().Be("terminatejob");
        doc.RootElement.GetProperty("onTaskFailure").GetString().Should().Be("performexitoptionsjobaction");
        doc.RootElement.GetProperty("poolInfo").GetProperty("poolId").GetString().Should().Be("gdal-heavy-pool");
    }

    [Fact]
    public void BuildCreateTaskPayload_IncludesContainerSettingsAndRetryConstraint()
    {
        var submission = new AzureBatchJobSubmission
        {
            AccountUrl = "https://acct.eastus.batch.azure.com",
            JobId = "honua-job-1",
            PoolId = "p",
            CommandLine = "/bin/bash -c run.sh",
            ContainerImage = "ghcr.io/honua/worker:1",
            ContainerRunOptions = "--rm",
            MaxTaskRetryCount = 3,
            TaskTimeout = TimeSpan.FromMinutes(90),
            EnvironmentSettings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HONUA_JOB_ID"] = "abc"
            }
        };

        var bytes = AzureBatchDataPlaneClient.BuildCreateTaskPayload(submission);
        using var doc = JsonDocument.Parse(bytes);

        doc.RootElement.GetProperty("id").GetString().Should().Be("honua-job-1");
        doc.RootElement.GetProperty("commandLine").GetString().Should().Be("/bin/bash -c run.sh");
        doc.RootElement.GetProperty("containerSettings").GetProperty("imageName").GetString()
            .Should().Be("ghcr.io/honua/worker:1");
        doc.RootElement.GetProperty("containerSettings").GetProperty("containerRunOptions").GetString()
            .Should().Be("--rm");
        doc.RootElement.GetProperty("constraints").GetProperty("maxTaskRetryCount").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("constraints").GetProperty("maxWallClockTime").GetString().Should().StartWith("PT");

        var envSettings = doc.RootElement.GetProperty("environmentSettings");
        envSettings.GetArrayLength().Should().Be(1);
        envSettings[0].GetProperty("name").GetString().Should().Be("HONUA_JOB_ID");
        envSettings[0].GetProperty("value").GetString().Should().Be("abc");
    }

    [Fact]
    public void BuildCreateTaskPayload_IncludesOutputFilesWhenContainerUrlSupplied()
    {
        var submission = new AzureBatchJobSubmission
        {
            AccountUrl = "https://acct.eastus.batch.azure.com",
            JobId = "honua-job-1",
            PoolId = "p",
            CommandLine = "/bin/bash -c run.sh",
            OutputContainerUrl = "https://acct.blob.core.windows.net/artifacts?sv=..."
        };

        var bytes = AzureBatchDataPlaneClient.BuildCreateTaskPayload(submission);
        using var doc = JsonDocument.Parse(bytes);

        var outputs = doc.RootElement.GetProperty("outputFiles");
        outputs.GetArrayLength().Should().BeGreaterOrEqualTo(2);
        outputs[0].GetProperty("destination").GetProperty("container").GetProperty("containerUrl").GetString()
            .Should().StartWith("https://acct.blob.core.windows.net/artifacts");
    }

    [Theory]
    [InlineData("active", null, null, "Active")]
    [InlineData("preparing", null, null, "Preparing")]
    [InlineData("running", null, null, "Running")]
    [InlineData("completed", 0, null, "CompletedSuccess")]
    [InlineData("completed", 2, null, "CompletedFailure")]
    [InlineData("completed", 0, "task failed", "CompletedFailure")]
    [InlineData("unknown", null, null, "Active")]
    [InlineData("", null, null, "Active")]
    public void MapExecutionState_MapsRawStateAndFailureSignalsToCanonical(
        string rawState,
        int? exitCode,
        string? failureMessage,
        string expected)
    {
        var mapped = AzureBatchDataPlaneClient.MapExecutionState(rawState, exitCode, failureMessage);
        mapped.ToString().Should().Be(expected);
    }

    [Fact]
    public void ParseTaskState_ReadsExitCodeRetryCountAndFailureMessage()
    {
        const string json = """
        {
          "state": "completed",
          "executionInfo": {
            "exitCode": 137,
            "retryCount": 2,
            "result": "failure",
            "failureInfo": { "message": "Task was terminated by host" }
          }
        }
        """;

        using var document = JsonDocument.Parse(json);
        var parsed = AzureBatchDataPlaneClient.ParseTaskState("honua-job-1", document.RootElement);

        parsed.JobId.Should().Be("honua-job-1");
        parsed.ExecutionState.Should().Be(AzureBatchTaskExecutionState.CompletedFailure);
        parsed.ExitCode.Should().Be(137);
        parsed.RetryCount.Should().Be(2);
        parsed.FailureMessage.Should().Be("Task was terminated by host");
    }
}
