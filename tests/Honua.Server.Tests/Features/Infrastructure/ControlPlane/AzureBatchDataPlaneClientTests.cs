// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Azure.Core;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task CreateJobAsync_RecoversPartialSubmitWhenJobConflictsButTaskIsMissing()
    {
        // Simulates the partial-submit recovery case: a prior attempt created the Azure
        // Batch job but crashed before the task POST. On retry, the job POST returns 409
        // Conflict; the client must still issue the task POST so the reconciler does not
        // observe perpetual 404s on the missing task.
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths)
            .Enqueue(HttpStatusCode.Conflict, """{"code":"JobExists"}""")
            .Enqueue(HttpStatusCode.Created, string.Empty);

        var client = CreateClient(handler);
        var status = await client.CreateJobAsync(SampleSubmission());

        status.Should().Be(HttpStatusCode.Conflict, "prior job already exists so callers can resume observation");
        capturedPaths.Should().HaveCount(2);
        capturedPaths[0].Should().Contain("/jobs?");
        capturedPaths[1].Should().Contain("/jobs/honua-job-1/tasks?");
    }

    [Fact]
    public async Task CreateJobAsync_ReturnsConflictWhenBothJobAndTaskAlreadyExist()
    {
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths)
            .Enqueue(HttpStatusCode.Conflict, """{"code":"JobExists"}""")
            .Enqueue(HttpStatusCode.Conflict, """{"code":"TaskExists"}""");

        var client = CreateClient(handler);
        var status = await client.CreateJobAsync(SampleSubmission());

        status.Should().Be(HttpStatusCode.Conflict);
        capturedPaths.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateJobAsync_ReturnsCreatedWhenJobAndTaskAreFreshlyCreated()
    {
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths)
            .Enqueue(HttpStatusCode.Created, string.Empty)
            .Enqueue(HttpStatusCode.Created, string.Empty);

        var client = CreateClient(handler);
        var status = await client.CreateJobAsync(SampleSubmission());

        status.Should().Be(HttpStatusCode.Created);
        capturedPaths.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateJobAsync_DeletesFreshJobWhenTaskCreationReturnsFailure()
    {
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths)
            .Enqueue(HttpStatusCode.Created, string.Empty)
            .Enqueue(HttpStatusCode.BadGateway, """{"code":"TaskCreateFailed"}""")
            .Enqueue(HttpStatusCode.Accepted, string.Empty);

        var client = CreateClient(handler);
        var act = () => client.CreateJobAsync(SampleSubmission());

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.BadGateway);

        capturedPaths.Should().HaveCount(3);
        capturedPaths[0].Should().Contain("/jobs?");
        capturedPaths[1].Should().Contain("/jobs/honua-job-1/tasks?");
        capturedPaths[2].Should().Contain("/jobs/honua-job-1?");
    }

    [Fact]
    public async Task CreateJobAsync_DeletesFreshJobWhenTaskRequestThrows()
    {
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths)
            .Enqueue(HttpStatusCode.Created, string.Empty)
            .Enqueue(new HttpRequestException("task send failed"))
            .Enqueue(HttpStatusCode.Accepted, string.Empty);

        var client = CreateClient(handler);
        var act = () => client.CreateJobAsync(SampleSubmission());

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(ex => ex.Message.Contains("task send failed", StringComparison.Ordinal));

        capturedPaths.Should().HaveCount(3);
        capturedPaths[0].Should().Contain("/jobs?");
        capturedPaths[1].Should().Contain("/jobs/honua-job-1/tasks?");
        capturedPaths[2].Should().Contain("/jobs/honua-job-1?");
    }

    private static AzureBatchDataPlaneClient CreateClient(HttpMessageHandler handler)
        => new(
            new SingletonHttpClientFactory(new HttpClient(handler)),
            NullLogger<AzureBatchDataPlaneClient>.Instance,
            new StubTokenCredential());

    private static AzureBatchJobSubmission SampleSubmission()
        => new()
        {
            AccountUrl = "https://acct.eastus.batch.azure.com",
            JobId = "honua-job-1",
            PoolId = "gdal-heavy-pool",
            CommandLine = "/bin/bash -c run.sh"
        };

    private sealed class SingletonHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class QueuedHttpMessageHandler(List<string> capturedPaths) : HttpMessageHandler
    {
        private readonly Queue<object> _responses = new();

        public QueuedHttpMessageHandler Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });
            return this;
        }

        public QueuedHttpMessageHandler Enqueue(Exception exception)
        {
            _responses.Enqueue(exception);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capturedPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            var next = _responses.Dequeue();
            if (next is Exception exception)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }

            return Task.FromResult((HttpResponseMessage)next);
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("stub-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1)));
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
