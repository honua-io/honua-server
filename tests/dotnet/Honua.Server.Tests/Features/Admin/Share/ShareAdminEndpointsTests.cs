// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Server.Features.Admin.Share;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin.Share;

/// <summary>
/// Integration tests for Console Share export definitions, run history, and traffic APIs (#1216).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class ShareAdminEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly InMemoryShareExportStore _exportStore = new();
    private readonly InMemoryShareTrafficStore _trafficStore = new();
    private readonly RecordingJobStore _jobStore = new();
    private readonly RecordingJobQueue _jobQueue = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ShareAdminEndpointsTests()
    {
        SeedTraffic();
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IShareExportStore>();
                services.RemoveAll<IShareTrafficStore>();
                services.RemoveAll<IShareExportDestinationResolver>();
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IJobQueue>();
                services.AddSingleton<IShareExportStore>(_exportStore);
                services.AddSingleton<IShareTrafficStore>(_trafficStore);
                services.AddSingleton<IShareExportDestinationResolver, TestDestinationResolver>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IJobQueue>(_jobQueue);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports")]
    public async Task CreateDefinition_WithSnakeAndKebabSecretMaterialKeys_ReturnsBadRequest()
    {
        string[] secretKeys = ["api_key", "access-key", "private_key"];
        foreach (var secretKey in secretKeys)
        {
            var response = await _client.PostAsJsonAsync(
                "/api/v1/admin/share/exports",
                BuildRequest(
                    $"secret-{secretKey.Replace('_', '-')}",
                    "Webhook",
                    destinationConfig: new Dictionary<string, string>
                    {
                        [secretKey] = "raw-secret-material"
                    }),
                JsonOptions);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, secretKey);
            using var problem = await ReadJsonAsync(response);
            problem.RootElement.GetProperty("detail").GetString().Should().Contain($"destinationConfig.{secretKey}");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}")]
    public async Task GetDefinition_WithStoredSnakeAndKebabSecretMaterialKeys_RedactsValues()
    {
        var definition = BuildDefinitionRecord(
            "redaction-definition",
            "redaction-layer",
            DateTimeOffset.Parse("2026-05-25T00:00:00.0009000Z", CultureInfo.InvariantCulture),
            new Dictionary<string, string>
            {
                ["api_key"] = "raw-api-key",
                ["access-key"] = "raw-access-key",
                ["private_key"] = "raw-private-key",
                ["secret_ref"] = "vault://share/secret"
            });
        await _exportStore.CreateDefinitionAsync(definition);

        var response = await _client.GetAsync($"/api/v1/admin/share/exports/{definition.ExportId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var config = doc.RootElement.GetProperty("destinationConfig");
        config.GetProperty("api_key").GetString().Should().Be("redacted");
        config.GetProperty("access-key").GetString().Should().Be("redacted");
        config.GetProperty("private_key").GetString().Should().Be("redacted");
        config.GetProperty("secret_ref").GetString().Should().Be("vault://share/secret");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports")]
    [Endpoint("GET /api/v1/admin/share/exports")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}")]
    [Endpoint("PUT /api/v1/admin/share/exports/{exportId}")]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/pause")]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/resume")]
    [Endpoint("DELETE /api/v1/admin/share/exports/{exportId}")]
    public async Task ExportDefinitionLifecycle_CreateListUpdatePauseResumeDelete_RoundTrips()
    {
        var created = await CreateDefinitionAsync("lifecycle-layer", "Webhook");
        var exportId = created.GetProperty("exportId").GetString();
        exportId.Should().NotBeNullOrWhiteSpace();
        created.GetProperty("destinationStatus").GetString().Should().Be("Supported");

        var list = await _client.GetAsync("/api/v1/admin/share/exports?destinationType=Webhook&limit=10");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(list))
        {
            doc.RootElement.GetProperty("items")
                .EnumerateArray()
                .Should()
                .Contain(item => item.GetProperty("exportId").GetString() == exportId);
        }

        var get = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(get))
        {
            doc.RootElement.GetProperty("serviceName").GetString().Should().Be("lifecycle-layer");
        }

        var update = await _client.PutAsJsonAsync(
            $"/api/v1/admin/share/exports/{exportId}",
            BuildRequest("lifecycle-layer", "Webhook", displayName: "Updated export", scheduleState: "Paused"),
            JsonOptions);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(update))
        {
            doc.RootElement.GetProperty("displayName").GetString().Should().Be("Updated export");
            doc.RootElement.GetProperty("scheduleState").GetString().Should().Be("Paused");
        }

        var pause = await _client.PostAsync($"/api/v1/admin/share/exports/{exportId}/pause", null);
        pause.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(pause))
        {
            doc.RootElement.GetProperty("scheduleState").GetString().Should().Be("Paused");
        }

        var resume = await _client.PostAsync($"/api/v1/admin/share/exports/{exportId}/resume", null);
        resume.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(resume))
        {
            doc.RootElement.GetProperty("scheduleState").GetString().Should().Be("Active");
        }

        var delete = await _client.DeleteAsync($"/api/v1/admin/share/exports/{exportId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var missing = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/share/exports")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs")]
    public async Task CursorPagination_WithSubMillisecondTimestamps_ReturnsNextDefinitionAndRun()
    {
        var baseTime = DateTimeOffset.Parse("2026-05-25T03:00:00Z", CultureInfo.InvariantCulture);
        var older = baseTime.AddTicks(800);
        var newer = baseTime.AddTicks(900);
        var olderDefinition = BuildDefinitionRecord("cursor-definition-a", "cursor-layer", older);
        var newerDefinition = BuildDefinitionRecord("cursor-definition-b", "cursor-layer", newer);
        await _exportStore.CreateDefinitionAsync(olderDefinition);
        await _exportStore.CreateDefinitionAsync(newerDefinition);

        var firstDefinitions = await _client.GetAsync("/api/v1/admin/share/exports?serviceName=cursor-layer&limit=1");

        firstDefinitions.StatusCode.Should().Be(HttpStatusCode.OK);
        string? definitionCursor;
        using (var doc = await ReadJsonAsync(firstDefinitions))
        {
            doc.RootElement.GetProperty("items")[0].GetProperty("exportId").GetString().Should().Be(newerDefinition.ExportId);
            definitionCursor = doc.RootElement.GetProperty("nextCursor").GetString();
        }

        var nextDefinitions = await _client.GetAsync(
            $"/api/v1/admin/share/exports?serviceName=cursor-layer&limit=1&cursor={Uri.EscapeDataString(definitionCursor!)}");

        nextDefinitions.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(nextDefinitions))
        {
            doc.RootElement.GetProperty("items")[0].GetProperty("exportId").GetString().Should().Be(olderDefinition.ExportId);
        }

        await _exportStore.AppendRunAsync(BuildRunRecord(newerDefinition.ExportId, "cursor-run-a", baseTime.AddTicks(100)));

        var definitionsAfterOlderRun = await _client.GetAsync("/api/v1/admin/share/exports?serviceName=cursor-layer&limit=1");
        definitionsAfterOlderRun.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(definitionsAfterOlderRun))
        {
            doc.RootElement.GetProperty("items")[0].GetProperty("exportId").GetString().Should().Be(newerDefinition.ExportId);
        }

        await _exportStore.AppendRunAsync(BuildRunRecord(newerDefinition.ExportId, "cursor-run-b", newer));

        var firstRuns = await _client.GetAsync($"/api/v1/admin/share/exports/{newerDefinition.ExportId}/runs?limit=1");

        firstRuns.StatusCode.Should().Be(HttpStatusCode.OK);
        string? runCursor;
        using (var doc = await ReadJsonAsync(firstRuns))
        {
            doc.RootElement.GetProperty("items")[0].GetProperty("runId").GetString().Should().Be("cursor-run-b");
            runCursor = doc.RootElement.GetProperty("nextCursor").GetString();
        }

        var nextRuns = await _client.GetAsync(
            $"/api/v1/admin/share/exports/{newerDefinition.ExportId}/runs?limit=1&cursor={Uri.EscapeDataString(runCursor!)}");

        nextRuns.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(nextRuns))
        {
            doc.RootElement.GetProperty("items")[0].GetProperty("runId").GetString().Should().Be("cursor-run-a");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/trigger")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs/{runId}")]
    public async Task Trigger_SupportedDestination_CreatesRunWithJobRunId()
    {
        var created = await CreateDefinitionAsync("job-link-layer", "Webhook");
        var exportId = created.GetProperty("exportId").GetString()!;

        var trigger = await _client.PostAsync($"/api/v1/admin/share/exports/{exportId}/trigger", null);

        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var triggerDoc = await ReadJsonAsync(trigger);
        var run = triggerDoc.RootElement;
        var runId = run.GetProperty("runId").GetString();
        var jobRunId = run.GetProperty("jobRunId").GetString();
        run.GetProperty("status").GetString().Should().Be("Queued");
        jobRunId.Should().NotBeNullOrWhiteSpace();

        var job = await _jobStore.GetAsync(jobRunId!);
        job.Should().NotBeNull();
        job!.OperationId.Should().Be(jobRunId);
        job.Spec.Kind.Should().Be(ExecutionJobKind.ShareExport);
        job.Spec.Parameters[ExecutionJobParameterKeys.ShareExportId].Should().Be(exportId);
        job.Spec.Parameters[ExecutionJobParameterKeys.ShareRunId].Should().Be(runId);

        // Share runs are first-terminal-wins and do not model retry attempts, so the backing job must
        // opt out of the generic retry budget. Otherwise a Console retry could re-run the export while
        // run history stays frozen at its first terminal status.
        job.RetryPolicy.Should().Be(JobRetryPolicy.None);

        // The job must be dispatched onto the durable queue, not merely created, otherwise a
        // returned 202 would leave the run Queued with no worker able to claim it.
        _jobQueue.Enqueued.Should().ContainSingle().Which.Should().Be(jobRunId);

        var listRuns = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}/runs");
        listRuns.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(listRuns))
        {
            doc.RootElement.GetProperty("items")[0].GetProperty("jobRunId").GetString().Should().Be(jobRunId);
        }

        var detail = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}/runs/{runId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(detail))
        {
            doc.RootElement.GetProperty("jobRunId").GetString().Should().Be(jobRunId);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/trigger")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/cancel")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs/{runId}")]
    public async Task CancelBackingJob_ReconcilesShareRunToCancelled()
    {
        var created = await CreateDefinitionAsync("cancel-link-layer", "Webhook");
        var exportId = created.GetProperty("exportId").GetString()!;

        var trigger = await _client.PostAsync($"/api/v1/admin/share/exports/{exportId}/trigger", null);
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        string runId;
        string jobRunId;
        using (var triggerDoc = await ReadJsonAsync(trigger))
        {
            triggerDoc.RootElement.GetProperty("status").GetString().Should().Be("Queued");
            runId = triggerDoc.RootElement.GetProperty("runId").GetString()!;
            jobRunId = triggerDoc.RootElement.GetProperty("jobRunId").GetString()!;
        }

        // Cancel the backing Operate job through the jobs API before any worker claims it. The Share
        // docs promise this transitions the run; without terminal-callback notification on the cancel
        // path the run would remain Queued forever.
        var cancel = await _client.PostAsync($"/api/v1/admin/jobs/{jobRunId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _jobStore.GetAsync(jobRunId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);

        var detail = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}/runs/{runId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(detail))
        {
            doc.RootElement.GetProperty("status").GetString().Should().Be("Cancelled");
            doc.RootElement.GetProperty("jobRunId").GetString().Should().Be(jobRunId);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/trigger")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/cancel")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/actions")]
    public async Task RetryCancelledShareExportJob_IsRejectedAsNotRetryable()
    {
        // A Share export job is created with JobRetryPolicy.None. Cancelling it before any worker
        // pickup leaves it terminal at AttemptCount 0, where ShouldRetry(0) would be true for
        // MaxAttempts 1. The generic jobs API must still refuse retry: re-queuing the job would re-run
        // it while the first-terminal-wins run stays Cancelled, desyncing the Operate job and the run.
        var created = await CreateDefinitionAsync("retry-guard-layer", "Webhook");
        var exportId = created.GetProperty("exportId").GetString()!;

        var trigger = await _client.PostAsync($"/api/v1/admin/share/exports/{exportId}/trigger", null);
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        string runId;
        string jobRunId;
        using (var triggerDoc = await ReadJsonAsync(trigger))
        {
            runId = triggerDoc.RootElement.GetProperty("runId").GetString()!;
            jobRunId = triggerDoc.RootElement.GetProperty("jobRunId").GetString()!;
        }

        var cancel = await _client.PostAsync($"/api/v1/admin/jobs/{jobRunId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelledJob = await _jobStore.GetAsync(jobRunId);
        cancelledJob!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        cancelledJob.AttemptCount.Should().Be(0, "the job never reached a worker so the retry budget is untouched");

        // The retry action descriptor must advertise the job as not retryable (not "budget exhausted").
        var actions = await _client.GetAsync($"/api/v1/admin/jobs/{jobRunId}/actions");
        actions.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var actionsDoc = await ReadJsonAsync(actions))
        {
            var retry = actionsDoc.RootElement.GetProperty("actions")
                .EnumerateArray()
                .Single(action => action.GetProperty("name").GetString() == "retry");
            retry.GetProperty("allowed").GetBoolean().Should().BeFalse();
            retry.GetProperty("disabledReason").GetString().Should().Be("not retryable");
        }

        // The retry endpoint must reject with 409 rather than re-queuing the job.
        var retryResponse = await _client.PostAsync($"/api/v1/admin/jobs/{jobRunId}/retry", null);
        retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _jobStore.GetAsync(jobRunId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);

        // And the Share run must remain Cancelled — the rejected retry must not have reopened it.
        var detail = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}/runs/{runId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(detail))
        {
            doc.RootElement.GetProperty("status").GetString().Should().Be("Cancelled");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports")]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/trigger")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs")]
    public async Task Trigger_UnsupportedDestination_ReturnsProblemAndFailedRunWithoutJobLink()
    {
        var created = await CreateDefinitionAsync("unsupported-layer", "S3");
        created.GetProperty("destinationStatus").GetString().Should().Be("Unsupported");
        var exportId = created.GetProperty("exportId").GetString()!;

        var trigger = await _client.PostAsync($"/api/v1/admin/share/exports/{exportId}/trigger", null);

        trigger.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using (var problem = await ReadJsonAsync(trigger))
        {
            problem.RootElement.GetProperty("title").GetString().Should().Be("share-export-destination-unsupported");
        }

        var runs = await _client.GetAsync($"/api/v1/admin/share/exports/{exportId}/runs");
        runs.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(runs))
        {
            var run = doc.RootElement.GetProperty("items")[0];
            run.GetProperty("status").GetString().Should().Be("Failed");
            run.GetProperty("jobRunId").ValueKind.Should().Be(JsonValueKind.Null);
            run.GetProperty("lastError").GetString().Should().Be("share-export-destination-unsupported");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/share/traffic")]
    [Endpoint("GET /api/v1/admin/share/traffic/series")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic/series")]
    public async Task TrafficEndpoints_ReturnAggregateAndPerItemSummaryAndSeries()
    {
        const string range = "periodStart=2026-05-25T00:00:00Z&periodEnd=2026-05-25T02:00:00Z";

        var aggregate = await _client.GetAsync($"/api/v1/admin/share/traffic?{range}");
        aggregate.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(aggregate))
        {
            doc.RootElement.GetProperty("totalRequests").GetInt64().Should().Be(9);
            doc.RootElement.GetProperty("byInteractionType").GetProperty("openData").GetInt64().Should().Be(4);
        }

        var item = await _client.GetAsync($"/api/v1/admin/services/parcels/layers/7/share/traffic?{range}&resourceId=content-parcels");
        item.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(item))
        {
            doc.RootElement.GetProperty("itemRef").GetProperty("resourceId").GetString().Should().Be("content-parcels");
            doc.RootElement.GetProperty("totalRequests").GetInt64().Should().Be(5);
            doc.RootElement.GetProperty("byInteractionType").GetProperty("export").GetInt64().Should().Be(2);
        }

        var aggregateSeries = await _client.GetAsync($"/api/v1/admin/share/traffic/series?{range}&bucketMinutes=60");
        aggregateSeries.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(aggregateSeries))
        {
            doc.RootElement.GetProperty("buckets").GetArrayLength().Should().Be(2);
            doc.RootElement.GetProperty("buckets")[0].GetProperty("total").GetInt64().Should().Be(9);
        }

        var itemSeries = await _client.GetAsync($"/api/v1/admin/services/parcels/layers/7/share/traffic/series?{range}&resourceId=content-parcels&bucketMinutes=60");
        itemSeries.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(itemSeries))
        {
            doc.RootElement.GetProperty("buckets")[0].GetProperty("total").GetInt64().Should().Be(5);
            doc.RootElement.GetProperty("buckets")[1].GetProperty("total").GetInt64().Should().Be(0);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic/series")]
    public async Task PerItemTraffic_WithoutResourceId_MatchesServiceAndLayerBuckets()
    {
        const string range = "periodStart=2026-05-25T00:00:00Z&periodEnd=2026-05-25T02:00:00Z";

        // The seeded parcels/7 bucket carries resourceId "content-parcels". Omitting resourceId
        // must still match it (resourceId is an optional refinement), matching Postgres behavior.
        var summary = await _client.GetAsync($"/api/v1/admin/services/parcels/layers/7/share/traffic?{range}");
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(summary))
        {
            doc.RootElement.GetProperty("totalRequests").GetInt64().Should().Be(5);
            doc.RootElement.GetProperty("byInteractionType").GetProperty("export").GetInt64().Should().Be(2);
        }

        var series = await _client.GetAsync($"/api/v1/admin/services/parcels/layers/7/share/traffic/series?{range}&bucketMinutes=60");
        series.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(series))
        {
            doc.RootElement.GetProperty("buckets")[0].GetProperty("total").GetInt64().Should().Be(5);
            doc.RootElement.GetProperty("buckets")[1].GetProperty("total").GetInt64().Should().Be(0);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/share/traffic/series")]
    public async Task TrafficSeries_BucketCountBoundary_AllowsExactlyMaxAndRejectsOneTickOver()
    {
        // 2000 one-minute buckets from midnight (00:00 -> 33h20m later) is exactly the documented cap.
        var atMax = await _client.GetAsync(
            "/api/v1/admin/share/traffic/series?periodStart=2026-05-25T00:00:00Z&periodEnd=2026-05-26T09:20:00Z&bucketMinutes=1");
        atMax.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(atMax))
        {
            doc.RootElement.GetProperty("buckets").GetArrayLength().Should().Be(2000);
        }

        // One tick past that boundary would emit a 2001st bucket, so it must be rejected.
        var overByOneTick = await _client.GetAsync(
            "/api/v1/admin/share/traffic/series?periodStart=2026-05-25T00:00:00Z&periodEnd=2026-05-26T09:20:00.0000001Z&bucketMinutes=1");
        overByOneTick.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (var doc = await ReadJsonAsync(overByOneTick))
        {
            doc.RootElement.GetProperty("detail").GetString().Should().Contain("2000");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports")]
    [Endpoint("GET /api/v1/admin/share/exports")]
    public async Task ListDefinitions_ServiceNameFilter_IsCaseSensitive()
    {
        // serviceName is documented as an exact filter and Postgres compares service_name =
        // @service_name. The in-memory store must match ordinally so a different casing excludes the
        // definition, otherwise the fallback would hide casing bugs the durable provider would surface.
        var created = await CreateDefinitionAsync("Casing-Layer", "Webhook");
        var exportId = created.GetProperty("exportId").GetString()!;

        var mismatched = await _client.GetAsync("/api/v1/admin/share/exports?serviceName=casing-layer&limit=50");
        mismatched.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(mismatched))
        {
            doc.RootElement.GetProperty("items")
                .EnumerateArray()
                .Should()
                .NotContain(item => item.GetProperty("exportId").GetString() == exportId);
        }

        var exact = await _client.GetAsync("/api/v1/admin/share/exports?serviceName=Casing-Layer&limit=50");
        exact.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(exact))
        {
            doc.RootElement.GetProperty("items")
                .EnumerateArray()
                .Should()
                .Contain(item => item.GetProperty("exportId").GetString() == exportId);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic")]
    public async Task PerItemTraffic_ServiceNameMatch_IsCaseSensitive()
    {
        const string range = "periodStart=2026-05-25T00:00:00Z&periodEnd=2026-05-25T02:00:00Z";

        // The seeded bucket carries serviceName "parcels". A mismatched-casing path segment must not
        // match it; the per-item matcher compares serviceName ordinally, mirroring Postgres.
        var mismatched = await _client.GetAsync($"/api/v1/admin/services/Parcels/layers/7/share/traffic?{range}");
        mismatched.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(mismatched))
        {
            doc.RootElement.GetProperty("totalRequests").GetInt64().Should().Be(0);
        }

        var exact = await _client.GetAsync($"/api/v1/admin/services/parcels/layers/7/share/traffic?{range}");
        exact.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(exact))
        {
            doc.RootElement.GetProperty("totalRequests").GetInt64().Should().Be(5);
        }
    }

    private async Task<JsonElement> CreateDefinitionAsync(string serviceName, string destinationType)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/share/exports",
            BuildRequest(serviceName, destinationType),
            JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.Clone();
    }

    private static object BuildRequest(
        string serviceName,
        string destinationType,
        string? displayName = null,
        string? scheduleState = null,
        IReadOnlyDictionary<string, string>? destinationConfig = null)
        => new
        {
            resourceId = $"content-{serviceName}",
            serviceName,
            layerId = 7,
            displayName = displayName ?? $"{serviceName} export",
            destinationType,
            destinationConfig = destinationConfig ?? destinationType switch
            {
                "S3" => new Dictionary<string, string>
                {
                    ["bucket"] = "share-exports",
                    ["credentialRef"] = "vault://share/s3"
                },
                _ => new Dictionary<string, string>
                {
                    ["url"] = "https://example.invalid/share-webhook",
                    ["credentialRef"] = "vault://share/webhook"
                }
            },
            format = "GeoJSON",
            schedule = "0 * * * *",
            scheduleState
        };

    private static ShareExportDefinition BuildDefinitionRecord(
        string exportId,
        string serviceName,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, string>? destinationConfig = null)
        => new()
        {
            ExportId = exportId,
            ResourceId = $"content-{serviceName}",
            ServiceName = serviceName,
            LayerId = 7,
            DisplayName = $"{serviceName} export",
            DestinationType = ShareExportDestinationType.Webhook,
            DestinationStatus = ShareExportDestinationStatus.Supported,
            DestinationConfig = destinationConfig ?? new Dictionary<string, string>
            {
                ["url"] = "https://example.invalid/share-webhook",
                ["credentialRef"] = "vault://share/webhook"
            },
            Format = "GeoJSON",
            Schedule = "0 * * * *",
            ScheduleState = ShareExportScheduleState.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };

    private static ShareExportRun BuildRunRecord(string exportId, string runId, DateTimeOffset triggeredAt)
        => new()
        {
            RunId = runId,
            ExportId = exportId,
            TriggerKind = ShareExportTriggerKind.Manual,
            Status = ShareExportRunStatus.Queued,
            JobRunId = null,
            TriggeredAt = triggeredAt,
            StartedAt = null,
            CompletedAt = null,
            TargetSummary = "Webhook https://example.invalid/share-webhook",
            ResultArtifacts = Array.Empty<string>(),
            LastError = null
        };

    private void SeedTraffic()
    {
        var bucketStart = DateTimeOffset.Parse("2026-05-25T00:00:00Z", CultureInfo.InvariantCulture);
        _trafficStore.AddBucket(
            new ShareItemRef { ResourceId = "content-parcels", ServiceName = "parcels", LayerId = 7 },
            bucketStart,
            new ShareTrafficCounts { Public = 3, Export = 2 });
        _trafficStore.AddBucket(
            new ShareItemRef { ResourceId = "content-roads", ServiceName = "roads", LayerId = 2 },
            bucketStart,
            new ShareTrafficCounts { OpenData = 4 });
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private sealed class TestDestinationResolver : IShareExportDestinationResolver
    {
        public ShareExportDestinationStatus Resolve(ShareExportDestinationType destinationType)
            => destinationType switch
            {
                ShareExportDestinationType.Webhook => ShareExportDestinationStatus.Supported,
                ShareExportDestinationType.AuditSnapshot => ShareExportDestinationStatus.NotConfigured,
                _ => ShareExportDestinationStatus.Unsupported
            };
    }

    private sealed class RecordingJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_jobs.ContainsKey(job.OperationId))
            {
                return Task.FromResult(false);
            }

            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage
            {
                Items = _jobs.Values
                    .Where(job => !query.Kind.HasValue || job.Spec.Kind == query.Kind.Value)
                    .OrderByDescending(job => job.CreatedAt)
                    .Take(query.Limit)
                    .ToArray(),
                NextCursor = null
            });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(
                _jobs.Values.Where(job => !kind.HasValue || job.Spec.Kind == kind.Value).ToArray());
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Enqueued { get; } = [];

        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<long>(0);
    }
}

/// <summary>
/// Verifies Share traffic endpoints keep durable traffic-store outages retryable.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class ShareAdminTrafficUnavailableTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ShareAdminTrafficUnavailableTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IShareTrafficStore>();
                services.AddSingleton<IShareTrafficStore, ThrowingTrafficStore>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/share/traffic")]
    [Endpoint("GET /api/v1/admin/share/traffic/series")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic/series")]
    public async Task TrafficEndpoints_WhenStoreUnavailable_ReturnServiceUnavailable()
    {
        const string range = "periodStart=2026-05-25T00:00:00Z&periodEnd=2026-05-25T02:00:00Z";
        string[] urls =
        [
            $"/api/v1/admin/share/traffic?{range}",
            $"/api/v1/admin/share/traffic/series?{range}&bucketMinutes=60",
            $"/api/v1/admin/services/parcels/layers/7/share/traffic?{range}&resourceId=content-parcels",
            $"/api/v1/admin/services/parcels/layers/7/share/traffic/series?{range}&resourceId=content-parcels&bucketMinutes=60"
        ];

        foreach (var url in urls)
        {
            var response = await _client.GetAsync(url);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, url);
            using var problem = await ReadJsonAsync(response);
            problem.RootElement.GetProperty("title").GetString().Should().Be("Service Unavailable");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private sealed class ThrowingTrafficStore : IShareTrafficStore
    {
        public Task<ShareTrafficSummary> GetSummaryAsync(
            ShareTrafficQuery query,
            CancellationToken cancellationToken = default)
            => throw new ShareTrafficStoreUnavailableException("Share traffic store is unavailable.");

        public Task<ShareTrafficSeries> GetSeriesAsync(
            ShareTrafficQuery query,
            CancellationToken cancellationToken = default)
            => throw new ShareTrafficStoreUnavailableException("Share traffic store is unavailable.");
    }
}

/// <summary>
/// Verifies a Supported trigger whose dispatch to the job queue fails with a cancellation-class
/// (request-abort) exception still rolls the created job back to a terminal state and records a
/// Failed run, rather than letting the cancellation bypass compensation and strand a Queued job no
/// worker runs (#1216 request-abort regression). A generic dispatch failure is exercised by the
/// run-persist failure suite's shared compensation path.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class ShareExportDispatchFailureTests : IAsyncLifetime
{
    private readonly InMemoryShareExportStore _exportStore = new();
    private readonly RollbackTrackingJobStore _jobStore = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ShareExportDispatchFailureTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IShareExportStore>();
                services.RemoveAll<IShareExportDestinationResolver>();
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IJobQueue>();
                services.AddSingleton<IShareExportStore>(_exportStore);
                services.AddSingleton<IShareExportDestinationResolver, SupportedDestinationResolver>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IJobQueue, ThrowingJobQueue>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/trigger")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs")]
    public async Task Trigger_WhenDispatchThrowsCancellation_RollsBackJobAndRecordsFailedRun()
    {
        var definition = new ShareExportDefinition
        {
            ExportId = "dispatch-failure-export",
            ResourceId = "content-dispatch",
            ServiceName = "dispatch-layer",
            LayerId = 7,
            DisplayName = "Dispatch failure export",
            DestinationType = ShareExportDestinationType.Webhook,
            DestinationStatus = ShareExportDestinationStatus.Supported,
            DestinationConfig = new Dictionary<string, string> { ["url"] = "https://example.invalid/webhook" },
            Format = "GeoJSON",
            Schedule = "0 * * * *",
            ScheduleState = ShareExportScheduleState.Active,
            CreatedAt = DateTimeOffset.Parse("2026-05-25T00:00:00Z", CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse("2026-05-25T00:00:00Z", CultureInfo.InvariantCulture)
        };
        await _exportStore.CreateDefinitionAsync(definition);

        var trigger = await _client.PostAsync($"/api/v1/admin/share/exports/{definition.ExportId}/trigger", null);

        // The cancellation-class dispatch failure must be compensated (not propagated): the endpoint
        // rolls the job back and records a Failed run, then returns 503.
        trigger.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var runs = await _client.GetAsync($"/api/v1/admin/share/exports/{definition.ExportId}/runs");
        runs.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(runs);
        var run = doc.RootElement.GetProperty("items")[0];
        run.GetProperty("status").GetString().Should().Be("Failed");
        run.GetProperty("lastError").GetString().Should().Be("share-export-dispatch-failed");
        var jobRunId = run.GetProperty("jobRunId").GetString();
        jobRunId.Should().NotBeNullOrWhiteSpace();

        // The created job must be rolled back to a terminal state, not left Queued.
        var job = await _jobStore.GetAsync(jobRunId!);
        job.Should().NotBeNull();
        job!.Status.Should().Be(ExecutionJobStatus.Failed);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private sealed class SupportedDestinationResolver : IShareExportDestinationResolver
    {
        public ShareExportDestinationStatus Resolve(ShareExportDestinationType destinationType)
            => ShareExportDestinationStatus.Supported;
    }

    // Throws a cancellation-class exception from dispatch. The post-create compensation must run for
    // OperationCanceledException too, not only for ordinary failures, otherwise an aborted dispatch
    // would bypass rollback and strand a Queued job/run.
    private sealed class ThrowingJobQueue : IJobQueue
    {
        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("Share export dispatch was canceled.");

        public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<long>(0);
    }

    private sealed class RollbackTrackingJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = _jobs.Values.ToArray(), NextCursor = null });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());
    }
}

/// <summary>
/// Verifies a Supported trigger whose run record cannot be persisted rolls the created job back to
/// a terminal state and never dispatches it, so a job is never claimable without a Share run.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class ShareExportRunPersistFailureTests : IAsyncLifetime
{
    private readonly RunAppendThrowingStore _exportStore = new();
    private readonly CapturingRollbackJobStore _jobStore = new();
    private readonly RecordingJobQueue _jobQueue = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ShareExportRunPersistFailureTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IShareExportStore>();
                services.RemoveAll<IShareExportDestinationResolver>();
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IJobQueue>();
                services.AddSingleton<IShareExportStore>(_exportStore);
                services.AddSingleton<IShareExportDestinationResolver, SupportedDestinationResolver>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IJobQueue>(_jobQueue);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports/{exportId}/trigger")]
    [Endpoint("GET /api/v1/admin/share/exports/{exportId}/runs")]
    public async Task Trigger_WhenRunPersistFails_RollsBackJobAndDoesNotDispatch()
    {
        var definition = new ShareExportDefinition
        {
            ExportId = "run-persist-failure-export",
            ResourceId = "content-run-persist",
            ServiceName = "run-persist-layer",
            LayerId = 7,
            DisplayName = "Run persist failure export",
            DestinationType = ShareExportDestinationType.Webhook,
            DestinationStatus = ShareExportDestinationStatus.Supported,
            DestinationConfig = new Dictionary<string, string> { ["url"] = "https://example.invalid/webhook" },
            Format = "GeoJSON",
            Schedule = "0 * * * *",
            ScheduleState = ShareExportScheduleState.Active,
            CreatedAt = DateTimeOffset.Parse("2026-05-25T00:00:00Z", CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse("2026-05-25T00:00:00Z", CultureInfo.InvariantCulture)
        };
        await _exportStore.SeedDefinitionAsync(definition);

        var trigger = await _client.PostAsync($"/api/v1/admin/share/exports/{definition.ExportId}/trigger", null);

        trigger.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The job must never be dispatched when the run cannot be persisted first.
        _jobQueue.Enqueued.Should().BeEmpty();

        // The created job must be rolled back to a terminal state, not left Queued for a worker.
        _jobStore.CreatedOperationId.Should().NotBeNullOrWhiteSpace();
        var job = await _jobStore.GetAsync(_jobStore.CreatedOperationId!);
        job.Should().NotBeNull();
        job!.Status.Should().Be(ExecutionJobStatus.Failed);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private sealed class SupportedDestinationResolver : IShareExportDestinationResolver
    {
        public ShareExportDestinationStatus Resolve(ShareExportDestinationType destinationType)
            => ShareExportDestinationStatus.Supported;
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Enqueued { get; } = [];

        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<long>(0);
    }

    // Persists definitions through an in-memory store but fails every run append, exercising the
    // pre-dispatch run-persist failure path.
    private sealed class RunAppendThrowingStore : IShareExportStore
    {
        private readonly InMemoryShareExportStore _inner = new();

        public Task<ShareExportDefinition> SeedDefinitionAsync(ShareExportDefinition definition) => _inner.CreateDefinitionAsync(definition);

        public Task<ShareExportRun> AppendRunAsync(ShareExportRun run, CancellationToken cancellationToken = default)
            => throw new ShareExportStoreUnavailableException("Share export store is unavailable.");

        public Task<ShareExportDefinitionPage> ListDefinitionsAsync(ShareExportDefinitionQuery query, CancellationToken cancellationToken = default)
            => _inner.ListDefinitionsAsync(query, cancellationToken);

        public Task<ShareExportDefinition?> GetDefinitionAsync(string exportId, CancellationToken cancellationToken = default)
            => _inner.GetDefinitionAsync(exportId, cancellationToken);

        public Task<ShareExportDefinition> CreateDefinitionAsync(ShareExportDefinition definition, CancellationToken cancellationToken = default)
            => _inner.CreateDefinitionAsync(definition, cancellationToken);

        public Task<ShareExportDefinition?> UpdateDefinitionAsync(ShareExportDefinition definition, CancellationToken cancellationToken = default)
            => _inner.UpdateDefinitionAsync(definition, cancellationToken);

        public Task<bool> DeleteDefinitionAsync(string exportId, CancellationToken cancellationToken = default)
            => _inner.DeleteDefinitionAsync(exportId, cancellationToken);

        public Task<ShareExportRun?> UpdateRunAsync(ShareExportRun run, CancellationToken cancellationToken = default)
            => _inner.UpdateRunAsync(run, cancellationToken);

        public Task<ShareExportRunPage> ListRunsAsync(string exportId, string? cursor, int limit, CancellationToken cancellationToken = default)
            => _inner.ListRunsAsync(exportId, cursor, limit, cancellationToken);

        public Task<ShareExportRun?> GetRunAsync(string exportId, string runId, CancellationToken cancellationToken = default)
            => _inner.GetRunAsync(exportId, runId, cancellationToken);
    }

    // Records the created job id and applies terminal rollback writes so the test can assert the
    // job ended Failed rather than lingering Queued.
    private sealed class CapturingRollbackJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public string? CreatedOperationId { get; private set; }

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            CreatedOperationId = job.OperationId;
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = _jobs.Values.ToArray(), NextCursor = null });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());
    }
}
