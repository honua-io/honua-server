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
                services.AddSingleton<IShareExportStore>(_exportStore);
                services.AddSingleton<IShareTrafficStore>(_trafficStore);
                services.AddSingleton<IShareExportDestinationResolver, TestDestinationResolver>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
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
