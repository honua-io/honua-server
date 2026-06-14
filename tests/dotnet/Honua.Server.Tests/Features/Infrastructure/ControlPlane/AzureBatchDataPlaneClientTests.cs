// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Honua.ControlPlane;
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

        // Azure Batch wildcard output paths prepend to each blob name, so the per-job id
        // must appear in both destinations to prevent same-named files (stdout.txt,
        // stderr.txt, repeated artifacts) from overwriting across jobs when the SAS points
        // at a shared container.
        foreach (var output in outputs.EnumerateArray())
        {
            var path = output.GetProperty("destination").GetProperty("container").GetProperty("path").GetString();
            path.Should().Contain(submission.JobId,
                "wildcard Azure Batch output uploads must namespace by job id to avoid container-level blob collisions");
        }
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

    [Fact]
    public async Task CreateJobAsync_DeletesFreshJobWhenTaskTokenAcquisitionFails()
    {
        // Regression: a credential that succeeds for the job POST and fails for the task
        // POST must still trigger partial-submit cleanup. Previously the task request was
        // built outside the guarded try/catch, so a second-token failure leaked the newly
        // created Azure Batch job. The scripted credential only fails on call #2 so the
        // cleanup DELETE (call #3) still mints a token and reaches the HTTP layer.
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths)
            .Enqueue(HttpStatusCode.Created, string.Empty)
            .Enqueue(HttpStatusCode.Accepted, string.Empty);

        var credential = new ScriptedTokenCredential(
            new AuthenticationFailedException("second token expired"),
            firstCallSucceeds: true,
            failOnCall: 2);

        var client = new AzureBatchDataPlaneClient(
            new SingletonHttpClientFactory(new HttpClient(handler)),
            NullLogger<AzureBatchDataPlaneClient>.Instance,
            credential);

        var act = () => client.CreateJobAsync(SampleSubmission());

        // Normalized as HttpRequestException so backends can share a single catch clause.
        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Message.Should().Contain("credential acquisition failed");
        thrown.Which.StatusCode.Should().BeNull("credential failures are ambiguous outcomes");

        // Both the original job POST and the cleanup DELETE must have been captured; the
        // task POST was never issued because the token fetch threw first.
        capturedPaths.Should().HaveCount(2);
        capturedPaths[0].Should().Contain("/jobs?");
        capturedPaths[1].Should().Contain("/jobs/honua-job-1?");
    }

    [Fact]
    public async Task CreateJobAsync_WrapsInitialTokenFailureAsHttpRequestException()
    {
        var capturedPaths = new List<string>();
        var handler = new QueuedHttpMessageHandler(capturedPaths);

        var credential = new ScriptedTokenCredential(
            new CredentialUnavailableException("managed identity unavailable"),
            firstCallSucceeds: false);

        var client = new AzureBatchDataPlaneClient(
            new SingletonHttpClientFactory(new HttpClient(handler)),
            NullLogger<AzureBatchDataPlaneClient>.Instance,
            credential);

        var act = () => client.CreateJobAsync(SampleSubmission());

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().BeNull();
        thrown.Which.InnerException.Should().BeOfType<CredentialUnavailableException>();
        capturedPaths.Should().BeEmpty("the HTTP layer is never reached when the credential cannot mint a token");
    }

    [Fact]
    public async Task GetJobStateAsync_WrapsCredentialFailureAsHttpRequestException()
    {
        // Observe paths must also normalize credential failures so the backend's
        // HttpRequestException catch preserves durable state instead of letting the raw
        // AuthenticationFailedException bubble up into the reconciler, where it would be
        // stamped as terminal Failed while the Azure Batch task is still live.
        var credential = new ScriptedTokenCredential(
            new AuthenticationFailedException("token expired"),
            firstCallSucceeds: false);
        var client = new AzureBatchDataPlaneClient(
            new SingletonHttpClientFactory(new HttpClient(new QueuedHttpMessageHandler(new List<string>()))),
            NullLogger<AzureBatchDataPlaneClient>.Instance,
            credential);

        var act = () => client.GetJobStateAsync("https://acct.eastus.batch.azure.com", "honua-job-1");

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task TerminateJobAsync_WrapsCredentialFailureAsHttpRequestException()
    {
        var credential = new ScriptedTokenCredential(
            new AuthenticationFailedException("token expired"),
            firstCallSucceeds: false);
        var client = new AzureBatchDataPlaneClient(
            new SingletonHttpClientFactory(new HttpClient(new QueuedHttpMessageHandler(new List<string>()))),
            NullLogger<AzureBatchDataPlaneClient>.Instance,
            credential);

        var act = () => client.TerminateJobAsync("https://acct.eastus.batch.azure.com", "honua-job-1");

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task GetPoolStateAsync_WrapsCredentialFailureAsHttpRequestException()
    {
        var credential = new ScriptedTokenCredential(
            new AuthenticationFailedException("token expired"),
            firstCallSucceeds: false);
        var client = new AzureBatchDataPlaneClient(
            new SingletonHttpClientFactory(new HttpClient(new QueuedHttpMessageHandler(new List<string>()))),
            NullLogger<AzureBatchDataPlaneClient>.Instance,
            credential);

        var act = () => client.GetPoolStateAsync("https://acct.eastus.batch.azure.com", "pool-1");

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().BeNull();
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

    private sealed class ScriptedTokenCredential(AuthenticationFailedException failureException, bool firstCallSucceeds, int? failOnCall = null) : TokenCredential
    {
        private int _callCount;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenCore();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetTokenCore());

        private AccessToken GetTokenCore()
        {
            var callIndex = Interlocked.Increment(ref _callCount);

            // When failOnCall is set, only that specific call fails; other calls succeed.
            // Otherwise fall back to the firstCallSucceeds toggle.
            if (failOnCall.HasValue)
            {
                if (callIndex == failOnCall.Value)
                {
                    throw failureException;
                }

                return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
            }

            if (callIndex == 1 && firstCallSucceeds)
            {
                return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
            }

            throw failureException;
        }
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
