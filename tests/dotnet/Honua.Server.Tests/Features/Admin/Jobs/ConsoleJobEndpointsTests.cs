// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin.Jobs;

/// <summary>
/// Integration tests for Console job observability endpoints (#1170).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class ConsoleJobEndpointsTests : IAsyncLifetime
{
    private readonly InMemoryJobStore _jobStore = new();
    private readonly InMemoryLogStore _logStore = new();
    private readonly RecordingJobQueue _jobQueue = new();
    private readonly RecordingBatchBackend _remoteBackend = new("remote", supportsRetry: true);
    private readonly RecordingBatchBackend _remoteNoRetryBackend = new("remote-no-retry", supportsRetry: false);
    private readonly StubArtifactStore _artifactStore = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ConsoleJobEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IExecutionLogStore>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IArtifactStore>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IExecutionLogStore>(_logStore);
                services.AddSingleton<IJobQueue>(_jobQueue);
                services.AddSingleton<IBatchComputeBackend>(_remoteBackend);
                services.AddSingleton<IBatchComputeBackend>(_remoteNoRetryBackend);
                services.AddSingleton<IArtifactStore>(_artifactStore);
            });
    }

    public async Task InitializeAsync()
    {
        SeedJobs();
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs")]
    public async Task ListJobs_WithFiltersAndCursor_ReturnsConsoleSummaries()
    {
        var response = await _client.GetAsync("/api/v1/admin/jobs?status=Running&queue=critical&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("jobId").GetString().Should().Be("job-running");
        items[0].GetProperty("status").GetString().Should().Be("Running");
        items[0].GetProperty("queue").GetString().Should().Be("critical");
        items[0].GetProperty("resourceRefs")[0].GetString().Should().Be("service/parcels");
        items[0].GetProperty("latestEvent").GetProperty("type").GetString().Should().Be("job.running");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs")]
    public async Task ListJobs_WithQueueFilter_ReturnsMatchingQueue()
    {
        var response = await _client.GetAsync("/api/v1/admin/jobs?queue=standard&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("jobId").GetString().Should().Be("job-running-standard");
        items[0].GetProperty("queue").GetString().Should().Be("standard");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs")]
    public async Task ListJobs_WithQueueFilter_ReturnsBackendFallbackQueue()
    {
        var response = await _client.GetAsync("/api/v1/admin/jobs?queue=fallback-queue&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("jobId").GetString().Should().Be("job-backend-queue");
        items[0].GetProperty("queue").GetString().Should().Be("fallback-queue");
        items[0].GetProperty("backend").GetString().Should().Be("fallback-queue");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/actions")]
    public async Task GetJobDetail_ForFailedJob_ReturnsFailureMetadataAndRetryAction()
    {
        var detail = await _client.GetAsync("/api/v1/admin/jobs/job-failed");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        detail.Headers.GetValues("X-Correlation-Id").Should().Contain("corr-failed");

        using (var doc = await ReadJsonAsync(detail))
        {
            var root = doc.RootElement;
            root.GetProperty("failure").GetProperty("classification").GetString().Should().Be("execution_failed");
            root.GetProperty("selectedMetadata").GetProperty(ExecutionJobParameterKeys.GeoprocessingPlanId)
                .GetString().Should().Be("plan-failed");
            root.GetProperty("stages")[0].GetProperty("name").GetString().Should().Be("Failed");
            root.GetProperty("links").GetProperty("logs").GetString().Should().EndWith("/api/v1/admin/jobs/job-failed/logs");
        }

        var actions = await _client.GetAsync("/api/v1/admin/jobs/job-failed/actions");
        actions.StatusCode.Should().Be(HttpStatusCode.OK);
        using var actionsDoc = await ReadJsonAsync(actions);
        var retry = actionsDoc.RootElement.GetProperty("actions")[0];
        retry.GetProperty("name").GetString().Should().Be("retry");
        retry.GetProperty("allowed").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    public async Task GetJobDetail_WithProcessSr_ExposesOnlyNormalizedWkid()
    {
        var validResponse = await _client.GetAsync("/api/v1/admin/jobs/job-process-sr");
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var valid = await ReadJsonAsync(validResponse))
        {
            var metadata = valid.RootElement.GetProperty("selectedMetadata");
            metadata.GetProperty(GeoprocessingProtocolMetadataKeys.GPServerProcessSr)
                .GetString().Should().Be("3857");
            metadata.TryGetProperty(GeoprocessingProtocolMetadataKeys.GPServerWorkspace, out _)
                .Should().BeFalse("workspace labels are not safe admin metadata");
        }

        var absentResponse = await _client.GetAsync("/api/v1/admin/jobs/job-succeeded");
        absentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var absent = await ReadJsonAsync(absentResponse))
        {
            absent.RootElement.GetProperty("selectedMetadata")
                .TryGetProperty(GeoprocessingProtocolMetadataKeys.GPServerProcessSr, out _)
                .Should().BeFalse();
        }

        var invalidResponse = await _client.GetAsync("/api/v1/admin/jobs/job-invalid-process-sr");
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var invalid = await ReadJsonAsync(invalidResponse);
        invalid.RootElement.GetProperty("selectedMetadata")
            .TryGetProperty(GeoprocessingProtocolMetadataKeys.GPServerProcessSr, out _)
            .Should().BeFalse("non-normalized job metadata must not reach the admin response");

        foreach (var jobId in new[] { "job-zero-process-sr", "job-negative-process-sr" })
        {
            var nonPositiveResponse = await _client.GetAsync($"/api/v1/admin/jobs/{jobId}");
            nonPositiveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var nonPositive = await ReadJsonAsync(nonPositiveResponse);
            nonPositive.RootElement.GetProperty("selectedMetadata")
                .TryGetProperty(GeoprocessingProtocolMetadataKeys.GPServerProcessSr, out _)
                .Should().BeFalse("WKIDs must be positive");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/logs")]
    public async Task GetJobLogs_PaginatesAndRejectsInvalidCursor()
    {
        _logStore.SetLogs("job-running",
            new ExecutionLogEntry { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2), Level = ExecutionLogLevel.Info, Message = "queued", Phase = "Queued" },
            new ExecutionLogEntry { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1), Level = ExecutionLogLevel.Warning, Message = "slow", Phase = "Running" });

        var first = await _client.GetAsync("/api/v1/admin/jobs/job-running/logs?limit=1");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var firstDoc = await ReadJsonAsync(first);
        firstDoc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        var cursor = firstDoc.RootElement.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrWhiteSpace();

        var second = await _client.GetAsync($"/api/v1/admin/jobs/job-running/logs?cursor={Uri.EscapeDataString(cursor!)}&limit=1");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        using var secondDoc = await ReadJsonAsync(second);
        secondDoc.RootElement.GetProperty("items")[0].GetProperty("message").GetString().Should().Be("slow");

        var invalid = await _client.GetAsync("/api/v1/admin/jobs/job-running/logs?cursor=not-a-cursor");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    public async Task GetJobDetail_WithPhaseHistory_EmitsOrderedMultiStageList()
    {
        var now = DateTimeOffset.UtcNow;
        _logStore.SetLogs("job-running",
            new ExecutionLogEntry { Timestamp = now.AddMinutes(-5), Level = ExecutionLogLevel.Info, Message = "queued", Phase = "Queued" },
            new ExecutionLogEntry { Timestamp = now.AddMinutes(-4), Level = ExecutionLogLevel.Info, Message = "provisioning workers", Phase = "Provisioning" },
            new ExecutionLogEntry { Timestamp = now.AddMinutes(-3), Level = ExecutionLogLevel.Info, Message = "step 1/2", Phase = "Reproject" },
            new ExecutionLogEntry { Timestamp = now.AddMinutes(-1), Level = ExecutionLogLevel.Info, Message = "step 2/2", Phase = "Reproject" });

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-running");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = await ReadJsonAsync(response);
        var stages = doc.RootElement.GetProperty("stages");

        // One stage per distinct contiguous phase (the two Reproject entries collapse into one).
        stages.GetArrayLength().Should().Be(3);
        stages[0].GetProperty("name").GetString().Should().Be("Queued");
        stages[0].GetProperty("status").GetString().Should().Be("Completed");
        stages[1].GetProperty("name").GetString().Should().Be("Provisioning");
        stages[1].GetProperty("status").GetString().Should().Be("Completed");
        // Last stage reflects the live job status (Running) rather than Completed.
        stages[2].GetProperty("name").GetString().Should().Be("Reproject");
        stages[2].GetProperty("status").GetString().Should().Be("Running");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    public async Task GetJobSteps_ProjectsSanitizedPerStepGlassBox()
    {
        var now = DateTimeOffset.UtcNow;
        _logStore.SetLogs("job-running",
            new ExecutionLogEntry
            {
                Timestamp = now.AddMinutes(-2),
                Level = ExecutionLogLevel.Info,
                Message = "Running gdalwarp on /tmp/honua-gdal-worker/job-running/input.tif",
                Phase = "Reproject",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["gdal.command"] = "gdalwarp --config AWS_SECRET_ACCESS_KEY worker-secret -t_srs EPSG:3857 /tmp/honua-gdal-worker/job-running/input.tif /tmp/honua-gdal-worker/job-running/out.tif",
                    ["targetSrs"] = "EPSG:3857"
                }
            },
            new ExecutionLogEntry
            {
                Timestamp = now.AddMinutes(-1),
                Level = ExecutionLogLevel.Info,
                Message = "Reproject complete",
                Phase = "Reproject"
            });

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-running/steps");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Correlation-Id").Should().Contain("corr-running");

        var rawBody = await response.Content.ReadAsStringAsync();
        // The unsanitized worker filesystem path must NEVER leak over HTTP.
        rawBody.Should().NotContain("honua-gdal-worker");
        rawBody.Should().NotContain("worker-secret");
        rawBody.Should().NotContain("/tmp/");

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        root.GetProperty("state").GetString().Should().Be("available");

        var steps = root.GetProperty("steps");
        steps.GetArrayLength().Should().Be(2);

        var first = steps[0];
        first.GetProperty("ordinal").GetInt32().Should().Be(0);
        first.GetProperty("phase").GetString().Should().Be("Reproject");
        first.GetProperty("message").GetString().Should().Contain("<path>");
        // Command is surfaced (sanitized) on its dedicated field.
        var command = first.GetProperty("command").GetString();
        command.Should().NotBeNull();
        command!.Should().Contain("gdalwarp");
        command.Should().Contain("<path>");
        command.Should().NotContain("honua-gdal-worker");
        // Command keys are not duplicated into the generic metadata bag.
        first.GetProperty("metadata").GetProperty("targetSrs").GetString().Should().Be("EPSG:3857");
        first.GetProperty("metadata").TryGetProperty("command", out _).Should().BeFalse();
        first.GetProperty("metadata").TryGetProperty("gdal.command", out _).Should().BeFalse();
        first.GetProperty("timingSource").GetString().Should().Be("inferred");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    public async Task GetJobSteps_SanitizesFallbackCurrentPhase()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-phase-secret", ExecutionJobStatus.Running, now, "corr-phase") with
        {
            CurrentPhase = "token=fallback-secret-value /tmp/worker/phase"
        });
        _logStore.SetLogs("job-phase-secret", new ExecutionLogEntry
        {
            Timestamp = now,
            Level = ExecutionLogLevel.Info,
            Message = "Running",
            Phase = ""
        });

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-phase-secret/steps");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("fallback-secret-value");
        body.Should().NotContain("/tmp/");
        body.Should().Contain("[REDACTED]");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    public async Task GetJobSteps_RequiresAdminAuthorization()
    {
        // The shared class fixture runs with dev-auth bypass (so the admin-keyed client used by
        // the other tests is always allowed), which means an anonymous call against it returns
        // 200. Admin gating (endpoints call .RequireAdminAuthorization()) is only exercised when
        // real gating is enabled, so stand up a dedicated fixture with HONUA_DEV_AUTH=false.
        // The same seeded in-memory stores are registered on it, so the steps the anonymous
        // caller is refused are real data an admin key can read back on the SAME host — which
        // is what separates "correctly refused" from "endpoint broken for everyone".
        const string GatedAdminPassword = "console-job-steps-admin-key";
        _logStore.SetLogs("job-running",
            new ExecutionLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2),
                Level = ExecutionLogLevel.Info,
                Message = "Reproject complete",
                Phase = "Reproject"
            });

        var gatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", GatedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IExecutionLogStore>(_logStore);
            });
        await gatedFixture.InitializeAsync();
        try
        {
            using var anonymous = gatedFixture.CreateClient();

            var response = await anonymous.GetAsync("/api/v1/admin/jobs/job-running/steps");

            // The admin policy names the ApiKey scheme, so an unauthenticated caller is
            // challenged: exactly 401, never 403 (authenticated-but-unprivileged) and never
            // 404 (which would leak whether the job exists).
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Nothing is disclosed: no step envelope, no correlation id, no phase names.
            var body = await response.Content.ReadAsStringAsync();
            foreach (var marker in new[] { "\"steps\"", "corr-running", "Reproject" })
            {
                body.Should().NotContain(marker, $"an unauthenticated caller must not see '{marker}'");
            }

            // Every response carries a per-request correlation id; the denial must not carry
            // the JOB's, which would confirm the job exists.
            if (response.Headers.TryGetValues("X-Correlation-Id", out var correlationIds))
            {
                correlationIds.Should().NotContain(
                    "corr-running",
                    "the denial must not echo the job's own correlation id");
            }

            // The admin principal reads the same job's steps off the SAME host, so the denial
            // above is a refusal of the caller and not an endpoint broken for everyone.
            using var admin = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", GatedAdminPassword));
            var adminResponse = await admin.GetAsync("/api/v1/admin/jobs/job-running/steps");
            var adminBody = await adminResponse.Content.ReadAsStringAsync();
            adminResponse.StatusCode.Should().Be(HttpStatusCode.OK, adminBody);
            using var adminDoc = JsonDocument.Parse(adminBody);
            adminDoc.RootElement.GetProperty("steps").GetArrayLength().Should().Be(1);
            adminDoc.RootElement.GetProperty("steps")[0].GetProperty("phase").GetString().Should().Be("Reproject");
        }
        finally
        {
            await gatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/cancel")]
    public async Task ListAndGetJob_ScopedAdminReadKeys_HonourReadGrantWithoutWideningExecute()
    {
        // Regression for #4981: admin:read (with or without admin:approve) must read
        // durable jobs like every other admin GET, while cancel/retry (Execute) stays
        // denied for a read-only scoped key. The shared class fixture runs with
        // dev-auth bypass, which would authenticate any X-API-Key as full admin and
        // never exercise a scoped key's real role/grants, so this stands up a
        // dedicated gated fixture (HONUA_DEV_AUTH=false) with its own seeded job.
        const string AdminPassword = "console-jobs-admin-bootstrap-key";
        var jobStore = new InMemoryJobStore();
        var now = DateTimeOffset.UtcNow;
        jobStore.Set(CreateJob("job-scoped-read", ExecutionJobStatus.Succeeded, now.AddMinutes(-5), "corr-scoped-read") with
        {
            CompletedAt = now.AddMinutes(-3),
            CurrentPhase = "Completed"
        });

        var gatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton<IExecutionJobStore>(jobStore);
            });
        await gatedFixture.InitializeAsync();
        try
        {
            using var adminClient = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            var readKey = await CreateApiKeyAsync(adminClient, "console-jobs-read-only", ["admin:read"]);
            var readApproveKey = await CreateApiKeyAsync(
                adminClient, "console-jobs-read-approve", ["admin:read", "admin:approve"]);
            var nonAdminKey = await CreateApiKeyAsync(adminClient, "console-jobs-write-only", ["write:parcels"]);

            using var readClient = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", readKey.Key));
            using var readApproveClient = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", readApproveKey.Key));
            using var nonAdminClient = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", nonAdminKey.Key));

            (await readClient.GetAsync("/api/v1/admin/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await readClient.GetAsync("/api/v1/admin/jobs/job-scoped-read")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await readApproveClient.GetAsync("/api/v1/admin/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await readApproveClient.GetAsync("/api/v1/admin/jobs/job-scoped-read")).StatusCode
                .Should().Be(HttpStatusCode.OK);

            (await nonAdminClient.GetAsync("/api/v1/admin/jobs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var cancel = await readClient.PostAsync("/api/v1/admin/jobs/job-scoped-read/cancel", null);
            cancel.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a read-only scoped key must not execute job control actions");
        }
        finally
        {
            await gatedFixture.DisposeAsync();
        }
    }

    private static async Task<AdminApiKeySecretResponse> CreateApiKeyAsync(
        HttpClient client, string name, IReadOnlyList<string> permissions)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest { Name = name, Permissions = permissions });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminApiKeySecretResponse>>();
        return result!.Data!;
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    public async Task GetJobSteps_ForUnknownJob_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/jobs/does-not-exist/steps");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task GetArtifacts_MapsAvailabilityStates()
    {
        var response = await _client.GetAsync("/api/v1/admin/jobs/job-artifacts/artifacts?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("artifactId").GetString()!,
            item => item.GetProperty("availability").GetString());

        items["available"].Should().Be("Available");
        items["missing"].Should().Be("Unavailable");
        items["expired"].Should().Be("Expired");
        items["data-uri"].Should().Be("Redacted");
        items["provider-error"].Should().Be("ProviderError");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task GetArtifacts_DirectReferencesRemainUsableWithMetadataProvider()
    {
        var job = (await _jobStore.GetAsync("job-artifacts"))!;
        var references = new[]
        {
            "https://example.test/results/output.json", "staging/job/a1/result/output.tif",
            "data:text/plain,private-inline", "https://example.test/output?token=private-value",
            "{\"outputType\":\"future\",\"storeReference\":\"private-store\"}",
            "https://fixture-user:749@example.test/output", "https://example.test/out\tput"
        };
        await _jobStore.SetAsync(job with { ArtifactReferences = references });
        var response = await _client.GetAsync("/api/v1/admin/jobs/job-artifacts/artifacts?limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        items.Should().HaveCount(7);
        for (var i = 0; i < 2; i++)
        {
            items[i].GetProperty("availability").GetString().Should().Be("Available");
            items[i].GetProperty("providerLink").GetString().Should().Be(references[i]);
        }
        foreach (var item in items.Skip(2))
        {
            item.GetProperty("availability").GetString().Should().Be("Redacted");
        }
        body.Should().NotContain("private-inline").And.NotContain("private-value").And.NotContain("private-store")
            .And.NotContain("fixture-user");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task GetArtifacts_ProviderFailureDoesNotEchoUnsafeReference()
    {
        var job = (await _jobStore.GetAsync("job-artifacts"))!;
        await _jobStore.SetAsync(job with
        {
            ArtifactReferences = ["https://provider-error.test/output?token=private-error-value"]
        });
        var response = await _client.GetAsync("/api/v1/admin/jobs/job-artifacts/artifacts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("items")[0];
        item.GetProperty("availability").GetString().Should().Be("ProviderError");
        item.GetProperty("artifactId").GetString().Should().StartWith("redacted-");
        body.Should().NotContain("private-error-value");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/cancel")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    public async Task ControlActions_CancelAndRetry_UpdateDurableJob()
    {
        var cancel = await _client.PostAsync("/api/v1/admin/jobs/job-cancel/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _jobStore.GetAsync("job-cancel"))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        _jobQueue.Removed.Should().Contain("job-cancel");

        var retry = await _client.PostAsync("/api/v1/admin/jobs/job-failed/retry", null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var retried = await _jobStore.GetAsync("job-failed");
        retried!.Status.Should().Be(ExecutionJobStatus.Queued);
        retried.ErrorMessage.Should().BeNull();
        _jobQueue.Requeued.Should().Contain("job-failed");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    public async Task Retry_WhenQueueWriteFails_RestoresTerminalJob()
    {
        _jobQueue.ThrowOnRequeue = true;

        var retry = await _client.PostAsync("/api/v1/admin/jobs/job-failed/retry", null);

        retry.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var failed = await _jobStore.GetAsync("job-failed");
        failed!.Status.Should().Be(ExecutionJobStatus.Failed);
        failed.CompletedAt.Should().NotBeNull();
        failed.ErrorMessage.Should().Be("Geometry validation failed.");
        failed.CurrentPhase.Should().Be("Failed");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/actions")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    public async Task Retry_RemoteBackend_UsesReconcilerRetryMarkerWithoutLocalQueue()
    {
        var actions = await _client.GetAsync("/api/v1/admin/jobs/job-remote-failed/actions");
        actions.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var actionsDoc = await ReadJsonAsync(actions))
        {
            var retryAction = actionsDoc.RootElement.GetProperty("actions")[0];
            retryAction.GetProperty("name").GetString().Should().Be("retry");
            retryAction.GetProperty("allowed").GetBoolean().Should().BeTrue();
        }

        var retry = await _client.PostAsync("/api/v1/admin/jobs/job-remote-failed/retry", null);

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var retried = await _jobStore.GetAsync("job-remote-failed");
        retried!.Status.Should().Be(ExecutionJobStatus.Queued);
        retried.AttemptCount.Should().Be(1);
        retried.NextRetryAt.Should().NotBeNull();
        retried.NextRetryAt!.Value.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
        retried.ProviderOperationId.Should().BeNull();
        retried.CompletedAt.Should().BeNull();
        retried.ErrorMessage.Should().BeNull();
        retried.ArtifactReferences.Should().BeEmpty();
        _jobQueue.Requeued.Should().NotContain("job-remote-failed");
        _remoteBackend.CapabilityRequests.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/actions")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    public async Task Retry_RemoteBackendWithoutRetrySupport_ReturnsConflict()
    {
        var actions = await _client.GetAsync("/api/v1/admin/jobs/job-remote-no-retry/actions");
        actions.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var actionsDoc = await ReadJsonAsync(actions))
        {
            var retryAction = actionsDoc.RootElement.GetProperty("actions")[0];
            retryAction.GetProperty("name").GetString().Should().Be("retry");
            retryAction.GetProperty("allowed").GetBoolean().Should().BeFalse();
            retryAction.GetProperty("disabledReason").GetString().Should().Be("job backend does not support retry");
        }

        var retry = await _client.PostAsync("/api/v1/admin/jobs/job-remote-no-retry/retry", null);

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var failed = await _jobStore.GetAsync("job-remote-no-retry");
        failed!.Status.Should().Be(ExecutionJobStatus.Failed);
        failed.ProviderOperationId.Should().Be("remote-provider-no-retry");
        _jobQueue.Requeued.Should().NotContain("job-remote-no-retry");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/cancel")]
    public async Task Cancel_WhenQueueRemovalFails_ReturnsDurableCancellation()
    {
        _jobQueue.ThrowOnRemove = true;

        var cancel = await _client.PostAsync("/api/v1/admin/jobs/job-cancel/cancel", null);

        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _jobStore.GetAsync("job-cancel"))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        _jobQueue.Removed.Should().Contain("job-cancel");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/events")]
    public async Task OperateEvents_FilterByCorrelation_ReturnsDurableJobEvent()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/events?kind=job&correlationId=corr-running");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var item = doc.RootElement.GetProperty("items")[0];
        item.GetProperty("operationId").GetString().Should().Be("job-running");
        item.GetProperty("correlationId").GetString().Should().Be("corr-running");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    public async Task GetSteps_NoisyJob_ReturnsBoundedTailWithAbsoluteOrdinalsAndTruncation()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-steps-bounded", ExecutionJobStatus.Running, now, "corr-steps"));
        _logStore.SetLogs("job-steps-bounded", Enumerable.Range(0, 603).Select(index => new ExecutionLogEntry
        {
            Timestamp = now.AddSeconds(index),
            Level = ExecutionLogLevel.Info,
            Message = $"event {index}"
        }).ToArray());
        var fullReads = _logStore.FullReads;
        using var response = await _client.GetAsync("/api/v1/admin/jobs/job-steps-bounded/steps");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(response);
        body.RootElement.GetProperty("steps").GetArrayLength().Should().Be(500);
        body.RootElement.GetProperty("steps")[0].GetProperty("ordinal").GetInt32().Should().Be(103);
        body.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(603);
        _logStore.FullReads.Should().Be(fullReads);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_PagesRetainedEventsWithoutReadingTheWholeStream()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-diag-paged", ExecutionJobStatus.Succeeded, now, "corr-paged") with { CompletedAt = now.AddMinutes(20) });
        _logStore.SetLogs("job-diag-paged", Enumerable.Range(0, 603).Select(i => new ExecutionLogEntry
        {
            Timestamp = now.AddSeconds(i),
            Level = ExecutionLogLevel.Info,
            Message = $"event {i}",
            Phase = "Run",
        }).ToArray());
        var fullReads = _logStore.FullReads;
        using var tailResponse = await _client.GetAsync("/api/v1/admin/jobs/job-diag-paged/diagnostics?events=2");
        tailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var tail = await ReadJsonAsync(tailResponse);
        tail.RootElement.GetProperty("events").GetProperty("items")[0].GetProperty("message").GetString().Should().Be("event 601");
        tail.RootElement.GetProperty("steps").GetArrayLength().Should().Be(500);
        tail.RootElement.GetProperty("evidence").GetProperty("truncated").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain(x => x!.StartsWith("steps and failure details:", StringComparison.Ordinal));
        var cursor = tail.RootElement.GetProperty("events").GetProperty("firstCursor").GetString();
        var messages = new List<string>();
        do
        {
            using var response = await _client.GetAsync("/api/v1/admin/jobs/job-diag-paged/diagnostics?events=200&eventCursor=" + Uri.EscapeDataString(cursor!));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var page = await ReadJsonAsync(response);
            var events = page.RootElement.GetProperty("events");
            events.GetProperty("mode").GetString().Should().Be("page");
            events.GetProperty("totalCount").GetInt32().Should().Be(603);
            messages.AddRange(events.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("message").GetString()!));
            cursor = events.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        messages.Should().Equal(Enumerable.Range(0, 603).Select(i => $"event {i}"));
        _logStore.FullReads.Should().Be(fullReads, "diagnostics must never materialize the complete log stream");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_RejectsMalformedEventCursor()
    {
        using var response = await _client.GetAsync("/api/v1/admin/jobs/job-failed/diagnostics?eventCursor=invalid!");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_ForFailedJob_ProjectsSanitizedFailureAndDisclosesMissingProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        _logStore.SetLogs("job-failed",
            new ExecutionLogEntry
            {
                Timestamp = now.AddMinutes(-5),
                Level = ExecutionLogLevel.Info,
                Message = "Resolving inputs",
                Phase = "Prepare"
            },
            new ExecutionLogEntry
            {
                Timestamp = now.AddMinutes(-4),
                Level = ExecutionLogLevel.Error,
                Message = "gdalwarp failed for /tmp/honua-gdal-worker/job-failed/input.tif",
                Phase = "Reproject",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["error.code"] = "GDAL_WARP_FAILED",
                    ["native.exit_code"] = "2",
                    ["stderr"] = "ERROR 1: PROJ: token=worker-secret could not open /tmp/honua-gdal-worker/job-failed/input.tif",
                    ["gdal.command"] = "gdalwarp -t_srs EPSG:3857 /tmp/honua-gdal-worker/job-failed/input.tif out.tif"
                }
            });

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-failed/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Correlation-Id").Should().Contain("corr-failed");

        var rawBody = await response.Content.ReadAsStringAsync();
        rawBody.Should().NotContain("honua-gdal-worker");
        rawBody.Should().NotContain("worker-secret");
        rawBody.Should().NotContain("/tmp/");

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        root.GetProperty("schema").GetString().Should().Be("honua.job-diagnostics/v1");
        root.GetProperty("view").GetString().Should().Be("operator");
        root.GetProperty("lifecycle").GetProperty("phase").GetString().Should().Be("failed");
        root.GetProperty("lifecycle").GetProperty("timingSource").GetString().Should().Be("inferred");
        root.GetProperty("worker").GetProperty("liveness").GetString().Should().Be("finished");

        var failure = root.GetProperty("failure");
        failure.GetProperty("classification").GetString().Should().Be("execution_failed");
        failure.GetProperty("phase").GetString().Should().Be("Reproject");
        failure.GetProperty("stepOrdinal").GetInt32().Should().Be(1);
        failure.GetProperty("errorCode").GetString().Should().Be("GDAL_WARP_FAILED");
        failure.GetProperty("nativeExitCode").GetInt32().Should().Be(2);
        failure.GetProperty("stderrExcerpt").GetString().Should().Contain("[REDACTED]");
        failure.GetProperty("stderrExcerpt").GetString().Should().Contain("<path>");
        failure.GetProperty("recovery").GetString().Should().Contain("retry");

        var attempt = root.GetProperty("attempt");
        attempt.GetProperty("number").GetInt32().Should().Be(1);
        attempt.GetProperty("maxAttempts").GetInt32().Should().Be(3);
        attempt.GetProperty("retry").GetProperty("allowed").GetBoolean().Should().BeTrue();
        attempt.GetProperty("retry").GetProperty("wouldRepeat").GetString().Should().Contain("attempt 2 of 3");

        // The managed job carries a plan and process definitions, so its release identity is
        // the server release plus that workflow binding: nothing is missing.
        var release = root.GetProperty("release");
        release.GetProperty("state").GetString().Should().Be("complete");
        release.GetProperty("planId").GetString().Should().Be("plan-failed");
        release.GetProperty("processDefinitions")[0].GetString().Should().Be("geometry.buffer");
        release.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();

        root.GetProperty("steps").GetArrayLength().Should().Be(2);
        root.GetProperty("steps")[1].GetProperty("command").GetString().Should().Contain("<path>");
        root.GetProperty("events").GetProperty("totalCount").GetInt32().Should().Be(2);
        root.GetProperty("events").GetProperty("truncated").GetBoolean().Should().BeFalse();
        root.GetProperty("artifacts").GetProperty("state").GetString().Should().Be("none");
        root.GetProperty("links").GetProperty("diagnostics").GetString().Should().EndWith("/api/v1/admin/jobs/job-failed/diagnostics");

        // The job record never recorded a claim, so the projection says the timings are
        // inferred instead of inventing a queue delay.
        var missing = root.GetProperty("evidence").GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray();
        missing.Should().Contain(m => m!.StartsWith("lifecycle.claimedAt", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_WithHarnessEvents_PreservesMeasuredAndInferredStepTiming()
    {
        var now = DateTimeOffset.UtcNow;
        _logStore.SetLogs("job-failed",
            new ExecutionLogEntry
            {
                Timestamp = now.AddMinutes(-5),
                Level = ExecutionLogLevel.Info,
                Message = "Executing tool",
                Phase = "Execute",
                Metadata = new Dictionary<string, string>
                {
                    [HarnessEvents.ElapsedKey] = "100"
                }
            },
            new ExecutionLogEntry
            {
                Timestamp = now.AddMinutes(-4),
                Level = ExecutionLogLevel.Error,
                Message = "Tool failed",
                Phase = "Execute",
                Metadata = new Dictionary<string, string>
                {
                    [HarnessEvents.ElapsedKey] = "600"
                }
            });

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-failed/diagnostics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var steps = doc.RootElement.GetProperty("steps");
        steps[0].GetProperty("durationMs").GetInt64().Should().Be(500);
        steps[0].GetProperty("timingSource").GetString().Should().Be("measured");
        steps[1].GetProperty("timingSource").GetString().Should().Be("inferred");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_ForRunningJobs_JudgesWorkerLivenessByHeartbeatPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(
            CreateJob("job-diag-alive", ExecutionJobStatus.Running, now.AddMinutes(-3), "corr-diag-alive") with
            {
                CurrentPhase = "Executing buffer",
                ClaimedBy = "worker-a",
                ClaimedAt = now.AddMinutes(-2),
                LastHeartbeatAt = now.AddSeconds(-5),
                AttemptCount = 1
            },
            CreateJob("job-diag-lost", ExecutionJobStatus.Running, now.AddMinutes(-10), "corr-diag-lost") with
            {
                CurrentPhase = "Uploading results",
                ClaimedBy = "worker-b",
                ClaimedAt = now.AddMinutes(-9),
                LastHeartbeatAt = now.AddMinutes(-5),
                HeartbeatPolicy = new JobHeartbeatPolicy { Interval = TimeSpan.FromSeconds(30), Timeout = TimeSpan.FromSeconds(90) },
                CancellationRequestedAt = now.AddMinutes(-1)
            });

        var alive = await _client.GetAsync("/api/v1/admin/jobs/job-diag-alive/diagnostics");
        alive.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(alive))
        {
            var root = doc.RootElement;
            root.GetProperty("lifecycle").GetProperty("phase").GetString().Should().Be("executing");
            root.GetProperty("lifecycle").GetProperty("timingSource").GetString().Should().Be("measured");
            root.GetProperty("lifecycle").GetProperty("queueDelayMs").GetInt64().Should().BeInRange(59_000, 61_000);
            root.GetProperty("worker").GetProperty("liveness").GetString().Should().Be("alive");
            root.GetProperty("worker").GetProperty("claimedBy").GetString().Should().Be("worker-a");
            root.GetProperty("attempt").GetProperty("retry").GetProperty("allowed").GetBoolean().Should().BeFalse();
            root.GetProperty("attempt").GetProperty("retry").GetProperty("disabledReason").GetString().Should().Contain("not terminal");
            root.TryGetProperty("failure", out _).Should().BeFalse();
        }

        var lost = await _client.GetAsync("/api/v1/admin/jobs/job-diag-lost/diagnostics");
        lost.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(lost))
        {
            var root = doc.RootElement;
            root.GetProperty("lifecycle").GetProperty("phase").GetString().Should().Be("publishing-results");
            root.GetProperty("worker").GetProperty("liveness").GetString().Should().Be("lost");
            root.GetProperty("worker").GetProperty("livenessReason").GetString().Should().Contain("90s heartbeat timeout");
            root.GetProperty("worker").GetProperty("heartbeatTimeoutSeconds").GetDouble().Should().Be(90);
        }
    }

    [IntegrationTheory]
    [InlineData("https://user:token=repo-secret@git.example.com/org/tools.git")]
    [InlineData("https://user:opaque-value@git.example.com/org/tools.git")]
    [InlineData("https://opaque-value@git.example.com/org/tools.git")]
    [InlineData("https://user:p%40ss%3Aword@git.example.com/org/tools.git")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_ForCustomCodeJob_ReportsCompleteReleaseIdentityWithoutCredentials(string repositoryUrl)
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-diag-customcode", ExecutionJobStatus.Failed, now.AddMinutes(-20), "corr-diag-customcode") with
        {
            CompletedAt = now.AddMinutes(-18),
            ClaimedAt = now.AddMinutes(-19),
            ClaimedBy = "worker-c",
            CurrentPhase = "Failed (heartbeat expired)",
            ErrorMessage = "Worker heartbeat expired after 3 attempt(s).",
            AttemptCount = 3,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 },
            Spec = CreateSpec("customcode-workload", new Dictionary<string, string>
            {
                [CustomCodeJobContract.RuntimeParam] = "python",
                [CustomCodeJobContract.RepoUrlParam] = repositoryUrl,
                [CustomCodeJobContract.GitRefParam] = "0123456789abcdef0123456789abcdef01234567",
                [CustomCodeJobContract.EntrypointParam] = "tools.buffer:execute",
                [CustomCodeJobContract.DepsManifestParam] = "requirements.lock",
                [GpProcessReleaseAdmission.ReleaseProcessParam] = "buffer-features",
                [GpProcessReleaseAdmission.ReleaseVersionParam] = "1.2.3",
                [GpProcessReleaseAdmission.ReleaseLockDigestParam] = "sha256:" + new string('a', 64),
                [GpProcessReleaseAdmission.ReleaseBindingVersionParam] = "4",
                [GpProcessReleaseAdmission.WorkflowVersionParam] = "workflow-v2",
                [GpProcessReleaseAdmission.DependencyDigestParam] = "sha256:" + new string('b', 64),
                [GpProcessReleaseAdmission.WorkerImageParam] = "registry/worker@sha256:" + new string('c', 64)
            }) with
            {
                RuntimeProfile = CustomCodeJobContract.RuntimeProfile
            }
        });

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-diag-customcode/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawBody = await response.Content.ReadAsStringAsync();
        rawBody.Should().NotContain("repo-secret");

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        var release = root.GetProperty("release");
        release.GetProperty("sourceRepository").GetString().Should().Be("[REDACTED]");
        release.GetProperty("state").GetString().Should().Be("complete");
        release.GetProperty("runtime").GetString().Should().Be("python");
        release.GetProperty("sourceRef").GetString().Should().Be("0123456789abcdef0123456789abcdef01234567");
        release.GetProperty("entrypoint").GetString().Should().Be("tools.buffer:execute");
        release.GetProperty("processRelease").GetString().Should().Be("buffer-features");
        release.GetProperty("processVersion").GetString().Should().Be("1.2.3");
        release.GetProperty("bindingVersion").GetString().Should().Be("4");
        release.GetProperty("lockDigest").GetString().Should().Be("sha256:" + new string('a', 64));
        release.GetProperty("dependencyLockDigest").GetString().Should().Be("sha256:" + new string('b', 64));
        release.GetProperty("workerImage").GetString().Should().EndWith("@sha256:" + new string('c', 64));
        release.GetProperty("missing").GetArrayLength().Should().Be(0);
        root.GetProperty("identity").GetProperty("runtimeProfile").GetString().Should().Be("custom-code");

        var failure = root.GetProperty("failure");
        failure.GetProperty("classification").GetString().Should().Be("worker_lost");
        failure.GetProperty("recovery").GetString().Should().Contain("retry budget is exhausted");
        root.GetProperty("attempt").GetProperty("number").GetInt32().Should().Be(3);
        root.GetProperty("attempt").GetProperty("retry").GetProperty("allowed").GetBoolean().Should().BeFalse();
        root.GetProperty("lifecycle").GetProperty("executionMs").GetInt64().Should().BeInRange(59_000, 61_000);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_DisclosesEventTruncationAndHistoricalJobsWithoutProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-diag-historical", ExecutionJobStatus.Succeeded, now.AddDays(-30), "corr-diag-historical") with
        {
            CompletedAt = now.AddDays(-30).AddMinutes(4),
            CurrentPhase = "Completed",
            ArtifactReferences = ["expired", "available"]
        });
        _logStore.SetLogs("job-diag-historical",
            Enumerable.Range(0, 5).Select(i => new ExecutionLogEntry
            {
                Timestamp = now.AddDays(-30).AddSeconds(i),
                Level = ExecutionLogLevel.Info,
                Message = $"event {i}",
                Phase = "Run"
            }).ToArray());

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-diag-historical/diagnostics?events=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var root = doc.RootElement;

        var events = root.GetProperty("events");
        events.GetProperty("totalCount").GetInt32().Should().Be(5);
        events.GetProperty("items").GetArrayLength().Should().Be(2);
        events.GetProperty("items")[1].GetProperty("message").GetString().Should().Be("event 4");
        events.GetProperty("truncated").GetBoolean().Should().BeTrue();

        root.GetProperty("release").GetProperty("state").GetString().Should().Be("unavailable");
        root.GetProperty("release").GetProperty("missing").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("release.planId");
        root.GetProperty("artifacts").GetProperty("state").GetString().Should().Be("partial");
        root.GetProperty("artifacts").GetProperty("expiredCount").GetInt32().Should().Be(1);

        var evidence = root.GetProperty("evidence");
        evidence.GetProperty("truncated").EnumerateArray().Select(e => e.GetString()!)
            .Should().Contain(t => t.StartsWith("events:", StringComparison.Ordinal));
        evidence.GetProperty("missing").EnumerateArray().Select(e => e.GetString()!)
            .Should().Contain(m => m.StartsWith("release.planId", StringComparison.Ordinal))
            .And.Contain(m => m.StartsWith("artifacts: 1 artifact(s) expired", StringComparison.Ordinal));

        var invalid = await _client.GetAsync("/api/v1/admin/jobs/job-diag-historical/diagnostics?events=0");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknown = await _client.GetAsync("/api/v1/admin/jobs/job-does-not-exist/diagnostics");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_ProjectsAdmittedHarnessIdentity()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var bytes = Encoding.UTF8.GetBytes("""
            {"v":1,"kind":"phase","seq":0,"attempt":"untrusted","correlation":"forged-corr","trace":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","phase":"execute"}
            {"v":1,"kind":"terminal","seq":1,"attempt":"untrusted","correlation":"forged-corr","status":"failed","exitCode":1}
            """);
        var now = DateTimeOffset.UtcNow;
        var job = CreateJob("job-diag-harness-identity", ExecutionJobStatus.Failed, now.AddMinutes(-5), "corr-admitted") with
        {
            CompletedAt = now.AddMinutes(-4),
            AttemptCount = 3,
            ErrorMessage = "tool failed",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                CorrelationId = "corr-admitted",
                TraceParent = "00-" + traceId + "-00f067aa0ba902b7-01"
            },
            Spec = CreateSpec("customcode-workload", new Dictionary<string, string>
            {
                [ExecutionJobParameterKeys.TraceId] = traceId
            })
        };
        var artifact = new CustomCodeArtifactDescriptor
        {
            JobId = job.OperationId,
            Attempt = 3,
            Name = HarnessEvents.FileName,
            ObjectKey = "server-owned/journal",
            ContentType = "application/x-ndjson",
            Provider = CloudStorageProvider.AwsS3,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            State = "available"
        };
        job = job with { ArtifactReferences = [artifact.Serialize()] };
        _jobStore.Set(job);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        storage.DownloadAsync(artifact.ObjectKey, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(bytes));
        using var services = new ServiceCollection()
            .AddSingleton<IExecutionJobStore>(_jobStore)
            .AddSingleton<IExecutionLogStore>(_logStore)
            .AddSingleton(storage)
            .BuildServiceProvider();
        var ingestor = new CustomCodeArtifactIngestor(services);
        await ingestor.IngestAsync(job, artifact, CancellationToken.None);
        await ingestor.IngestAsync(job, artifact, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-diag-harness-identity/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawBody = await response.Content.ReadAsStringAsync();
        rawBody.Should().NotContain("forged-corr").And.NotContain("untrusted")
            .And.NotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        root.GetProperty("correlationId").GetString().Should().Be("corr-admitted");
        root.GetProperty("traceId").GetString().Should().Be(traceId);
        root.GetProperty("attempt").GetProperty("number").GetInt32().Should().Be(3);
        var events = root.GetProperty("events");
        events.GetProperty("totalCount").GetInt32().Should().Be(2);
        events.GetProperty("items").EnumerateArray().Should().OnlyContain(item =>
            item.GetProperty("metadata").GetProperty(HarnessEvents.AttemptKey).GetString() == "3"
            && item.GetProperty("metadata").GetProperty(JobEventIdentity.JobMetadataKey).GetString() == job.OperationId
            && item.GetProperty("metadata").GetProperty(JobEventIdentity.CorrelationMetadataKey).GetString() == "corr-admitted"
            && item.GetProperty("metadata").GetProperty(JobEventIdentity.TraceMetadataKey).GetString() == traceId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_ScopedReadKey_ReceivesOwnerViewWithoutInfrastructureIdentities()
    {
        // Same gated-fixture pattern as the scoped admin read-key test above: a read-only
        // scoped key can read the projection, but the worker and provider identities that
        // only an execute-scoped operator needs are withheld and the view says so.
        const string AdminPassword = "console-job-diagnostics-admin-bootstrap-key";
        var jobStore = new InMemoryJobStore();
        var now = DateTimeOffset.UtcNow;
        jobStore.Set(CreateJob("job-diag-owner", ExecutionJobStatus.Running, now.AddMinutes(-2), "corr-diag-owner") with
        {
            CurrentPhase = "Running",
            ClaimedBy = "worker-internal-7",
            ClaimedAt = now.AddMinutes(-1),
            LastHeartbeatAt = now,
            ProviderOperationId = "provider-op-7",
            Spec = CreateSpec("private-workload", new Dictionary<string, string>
            {
                [GpProcessReleaseAdmission.WorkerImageParam] = "private.example/worker@sha256:" + new string('c', 64),
                [GpProcessReleaseAdmission.ReleaseLockDigestParam] = "sha256:" + new string('a', 64)
            })
        });

        var gatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionJobStore>(jobStore);
                services.AddSingleton<IExecutionLogStore>(new InMemoryLogStore());
            });
        await gatedFixture.InitializeAsync();
        try
        {
            using var admin = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            var readKey = await CreateApiKeyAsync(admin, "console-job-diagnostics-read-only", ["admin:read"]);

            using var reader = gatedFixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", readKey.Key));
            var response = await reader.GetAsync("/api/v1/admin/jobs/job-diag-owner/diagnostics");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            body.Should().NotContain("worker-internal-7");
            body.Should().NotContain("provider-op-7");
            body.Should().NotContain("private.example/worker");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            root.GetProperty("view").GetString().Should().Be("owner");
            root.GetProperty("worker").GetProperty("liveness").GetString().Should().Be("alive");
            root.GetProperty("worker").TryGetProperty("claimedBy", out _).Should().BeFalse();
            root.GetProperty("identity").TryGetProperty("providerOperationId", out _).Should().BeFalse();
            root.GetProperty("release").TryGetProperty("workerImage", out _).Should().BeFalse();
            root.GetProperty("release").GetProperty("lockDigest").GetString().Should().Be("sha256:" + new string('a', 64));
            root.GetProperty("evidence").GetProperty("notes").EnumerateArray().Select(e => e.GetString()!)
                .Should().Contain(n => n.StartsWith("owner view", StringComparison.Ordinal));

            var operatorResponse = await admin.GetAsync("/api/v1/admin/jobs/job-diag-owner/diagnostics");
            using var operatorDoc = await ReadJsonAsync(operatorResponse);
            operatorDoc.RootElement.GetProperty("view").GetString().Should().Be("operator");
            operatorDoc.RootElement.GetProperty("worker").GetProperty("claimedBy").GetString().Should().Be("worker-internal-7");
            operatorDoc.RootElement.GetProperty("release").GetProperty("workerImage").GetString()
                .Should().Be("private.example/worker@sha256:" + new string('c', 64));
        }
        finally
        {
            await gatedFixture.DisposeAsync();
        }
    }

    private void SeedJobs()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(
            CreateJob("job-running", ExecutionJobStatus.Running, now.AddMinutes(-3), "corr-running") with
            {
                CurrentPhase = "Running",
                PercentComplete = 42,
                Spec = CreateSpec("running-workload", new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.Queue] = "critical",
                    [ExecutionJobParameterKeys.ResourceRefs] = "service/parcels",
                    [ExecutionJobParameterKeys.TraceId] = "trace-running"
                })
            },
            CreateJob("job-succeeded", ExecutionJobStatus.Succeeded, now.AddMinutes(-8), "corr-succeeded") with
            {
                CompletedAt = now.AddMinutes(-5),
                CurrentPhase = "Completed"
            },
            CreateJob("job-process-sr", ExecutionJobStatus.Succeeded, now.AddMinutes(-8), "corr-process-sr") with
            {
                CompletedAt = now.AddMinutes(-5),
                CurrentPhase = "Completed",
                Spec = CreateSpec("process-sr-workload", new Dictionary<string, string>
                {
                    [GeoprocessingProtocolMetadataKeys.GPServerProcessSr] = "3857",
                    [GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = "tenant-private-workspace"
                })
            },
            CreateJob("job-invalid-process-sr", ExecutionJobStatus.Succeeded, now.AddMinutes(-8), "corr-invalid-process-sr") with
            {
                CompletedAt = now.AddMinutes(-5),
                CurrentPhase = "Completed",
                Spec = CreateSpec("invalid-process-sr-workload", new Dictionary<string, string>
                {
                    [GeoprocessingProtocolMetadataKeys.GPServerProcessSr] = "{\"wkid\":3857}"
                })
            },
            CreateJob("job-zero-process-sr", ExecutionJobStatus.Succeeded, now.AddMinutes(-8), "corr-zero-process-sr") with
            {
                CompletedAt = now.AddMinutes(-5),
                CurrentPhase = "Completed",
                Spec = CreateSpec("zero-process-sr-workload", new Dictionary<string, string>
                {
                    [GeoprocessingProtocolMetadataKeys.GPServerProcessSr] = "0"
                })
            },
            CreateJob("job-negative-process-sr", ExecutionJobStatus.Succeeded, now.AddMinutes(-8), "corr-negative-process-sr") with
            {
                CompletedAt = now.AddMinutes(-5),
                CurrentPhase = "Completed",
                Spec = CreateSpec("negative-process-sr-workload", new Dictionary<string, string>
                {
                    [GeoprocessingProtocolMetadataKeys.GPServerProcessSr] = "-4326"
                })
            },
            CreateJob("job-running-standard", ExecutionJobStatus.Running, now.AddMinutes(-1), "corr-running-standard") with
            {
                CurrentPhase = "Running",
                Spec = CreateSpec("standard-workload", new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.Queue] = "standard"
                })
            },
            CreateJob("job-backend-queue", ExecutionJobStatus.Running, now.AddMinutes(-7), "corr-backend-queue") with
            {
                CurrentPhase = "Running",
                Spec = CreateSpec("backend-queue-workload", backend: "fallback-queue")
            },
            CreateJob("job-failed", ExecutionJobStatus.Failed, now.AddMinutes(-6), "corr-failed") with
            {
                CompletedAt = now.AddMinutes(-4),
                CurrentPhase = "Failed",
                ErrorMessage = "Geometry validation failed.",
                AttemptCount = 1,
                RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 },
                Spec = CreateSpec("failed-workload", new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "plan-failed",
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "geometry.buffer"
                })
            },
            CreateJob("job-remote-failed", ExecutionJobStatus.Failed, now.AddMinutes(-5), "corr-remote-failed") with
            {
                CompletedAt = now.AddMinutes(-3),
                CurrentPhase = "Failed",
                ErrorMessage = "Remote provider failed.",
                AttemptCount = 1,
                ProviderOperationId = "remote-provider-1",
                RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 },
                ArtifactReferences = ["available"],
                Spec = CreateSpec("remote-failed-workload", backend: "remote")
            },
            CreateJob("job-remote-no-retry", ExecutionJobStatus.Failed, now.AddMinutes(-4), "corr-remote-no-retry") with
            {
                CompletedAt = now.AddMinutes(-2),
                CurrentPhase = "Failed",
                ErrorMessage = "Remote provider failed.",
                AttemptCount = 1,
                ProviderOperationId = "remote-provider-no-retry",
                RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 },
                Spec = CreateSpec("remote-no-retry-workload", backend: "remote-no-retry")
            },
            CreateJob("job-cancel", ExecutionJobStatus.Queued, now.AddMinutes(-2), "corr-cancel") with
            {
                CurrentPhase = "Queued"
            },
            CreateJob("job-cancelled", ExecutionJobStatus.Cancelled, now.AddMinutes(-10), "corr-cancelled") with
            {
                CompletedAt = now.AddMinutes(-9),
                CurrentPhase = "Cancelled"
            },
            CreateJob("job-artifacts", ExecutionJobStatus.Succeeded, now.AddMinutes(-12), "corr-artifacts") with
            {
                CompletedAt = now.AddMinutes(-11),
                ArtifactReferences = ["available", "missing", "expired", "data-uri", "provider-error"]
            });
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_NamesFailedPhaseAndAdmittedTrace()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(
            CreateJob("job-diag-input", ExecutionJobStatus.Failed, now.AddMinutes(-10), "corr-input") with
            {
                CompletedAt = now.AddMinutes(-9),
                CurrentPhase = "Failed",
                ErrorMessage = "missing or invalid input 'srid'; expected a positive integer",
                Audit = new OperationAuditInfo
                {
                    RequestedBy = "alice",
                    CorrelationId = "corr-input",
                    TraceParent = "00-" + traceId + "-00f067aa0ba902b7-01"
                }
            },
            CreateJob("job-diag-startup", ExecutionJobStatus.Failed, now.AddMinutes(-8), "corr-startup") with
            {
                CompletedAt = now.AddMinutes(-8),
                CurrentPhase = "Failed (submission)",
                ErrorMessage = "Submission failed."
            },
            CreateJob("job-diag-timed-out", ExecutionJobStatus.Failed, now.AddMinutes(-7), "corr-timeout") with
            {
                CompletedAt = now.AddMinutes(-6),
                CurrentPhase = "Failed (timeout)",
                ErrorMessage = "Execution timed out after 00:05:00."
            },
            CreateJob("job-diag-upload", ExecutionJobStatus.Failed, now.AddMinutes(-5), "corr-upload") with
            {
                CompletedAt = now.AddMinutes(-4),
                CurrentPhase = "Failed",
                ErrorMessage = "Failed to persist analysis-content artifacts; results would be incomplete."
            },
            CreateJob("job-diag-native", ExecutionJobStatus.Failed, now.AddMinutes(-4), "corr-native") with
            {
                CompletedAt = now.AddMinutes(-3),
                CurrentPhase = "Failed",
                ErrorMessage = "tool failed"
            },
            CreateJob("job-diag-expired", ExecutionJobStatus.Succeeded, now.AddMinutes(-3), "corr-expired") with
            {
                CompletedAt = now.AddMinutes(-2),
                CurrentPhase = "Completed",
                ArtifactReferences = ["expired"]
            },
            CreateJob("job-diag-license", ExecutionJobStatus.Failed, now.AddMinutes(-2), "corr-license") with
            {
                CompletedAt = now.AddMinutes(-1),
                CurrentPhase = "Failed",
                ErrorMessage = "license expired"
            });
        _logStore.SetLogs("job-diag-native", new ExecutionLogEntry
        {
            Timestamp = now.AddMinutes(-3),
            Level = ExecutionLogLevel.Error,
            Message = "failed (exit 7)",
            Phase = "execute",
            Metadata = new Dictionary<string, string> { ["honua.event.exitCode"] = "7" }
        });

        using (var doc = await ReadDiagnosticsAsync("job-diag-input"))
        {
            var root = doc.RootElement;
            root.GetProperty("traceId").GetString().Should().Be(traceId);
            root.GetProperty("evidence").GetProperty("missing").EnumerateArray().Select(e => e.GetString())
                .Should().NotContain(m => m!.StartsWith("traceId:", StringComparison.Ordinal));
            var failure = root.GetProperty("failure");
            failure.GetProperty("classification").GetString().Should().Be("execution_failed");
            failure.GetProperty("phase").GetString().Should().Be("executing");
            failure.GetProperty("recovery").GetString().Should().Contain("input was rejected");
        }

        using (var doc = await ReadDiagnosticsAsync("job-diag-startup"))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("traceId", out var trace))
            {
                trace.ValueKind.Should().Be(JsonValueKind.Null);
            }
            root.GetProperty("evidence").GetProperty("missing").EnumerateArray().Select(e => e.GetString())
                .Should().Contain(m => m!.StartsWith("traceId:", StringComparison.Ordinal));
            var failure = root.GetProperty("failure");
            failure.GetProperty("classification").GetString().Should().Be("execution_failed");
            failure.GetProperty("phase").GetString().Should().Be("starting-worker");
            failure.GetProperty("recovery").GetString().Should().Contain("did not start");
        }

        using (var doc = await ReadDiagnosticsAsync("job-diag-timed-out"))
        {
            var failure = doc.RootElement.GetProperty("failure");
            failure.GetProperty("classification").GetString().Should().Be("timeout");
            failure.GetProperty("phase").GetString().Should().Be("executing");
            failure.GetProperty("recovery").GetString().Should().Contain("timeout policy");
        }

        using (var doc = await ReadDiagnosticsAsync("job-diag-upload"))
        {
            var failure = doc.RootElement.GetProperty("failure");
            failure.GetProperty("classification").GetString().Should().Be("execution_failed");
            failure.GetProperty("phase").GetString().Should().Be("publishing-results");
            failure.GetProperty("recovery").GetString().Should().Contain("not stored");
        }

        using (var doc = await ReadDiagnosticsAsync("job-diag-native"))
        {
            var failure = doc.RootElement.GetProperty("failure");
            failure.GetProperty("classification").GetString().Should().Be("execution_failed");
            failure.GetProperty("phase").GetString().Should().Be("execute");
            failure.GetProperty("nativeExitCode").GetInt32().Should().Be(7);
            failure.GetProperty("recovery").GetString().Should().Contain("native output");
        }

        using (var doc = await ReadDiagnosticsAsync("job-diag-expired"))
        {
            var root = doc.RootElement;
            root.TryGetProperty("failure", out _).Should().BeFalse();
            root.GetProperty("artifacts").GetProperty("expiredCount").GetInt32().Should().Be(1);
            root.GetProperty("evidence").GetProperty("notes").EnumerateArray().Select(e => e.GetString())
                .Should().Contain(n => n!.Contains("expired results", StringComparison.Ordinal));
        }

        using (var doc = await ReadDiagnosticsAsync("job-diag-license"))
        {
            var failure = doc.RootElement.GetProperty("failure");
            failure.GetProperty("classification").GetString().Should().Be("execution_failed");
            failure.GetProperty("phase").GetString().Should().Be("Failed");
            failure.GetProperty("recovery").GetString().Should().NotContain("retention");
        }
    }

    private async Task<JsonDocument> ReadDiagnosticsAsync(string jobId)
    {
        var response = await _client.GetAsync($"/api/v1/admin/jobs/{jobId}/diagnostics");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static ExecutionJobRecord CreateJob(
        string id,
        ExecutionJobStatus status,
        DateTimeOffset createdAt,
        string correlationId)
        => new()
        {
            OperationId = id,
            Status = status,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(1),
            CurrentPhase = status.ToString(),
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                CorrelationId = correlationId
            },
            Spec = CreateSpec("default-workload")
        };

    private static ExecutionJobSpec CreateSpec(
        string workloadName,
        IReadOnlyDictionary<string, string>? parameters = null,
        string backend = "local")
        => new()
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = backend,
            WorkloadId = workloadName,
            WorkloadName = workloadName,
            Parameters = parameters ?? new Dictionary<string, string>()
        };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed class InMemoryJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public void Set(params ExecutionJobRecord[] jobs)
        {
            foreach (var job in jobs)
            {
                _jobs[job.OperationId] = job;
            }
        }

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
            _jobs[job.OperationId] = job with { Version = job.Version + 1 };
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job with { Version = job.Version + 1 };
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
        {
            var offset = DecodeOffset(query.Cursor);
            var filtered = _jobs.Values
                .Where(job => query.Statuses.Count == 0 || query.Statuses.Contains(job.Status))
                .Where(job => !query.Kind.HasValue || job.Spec.Kind == query.Kind.Value)
                .Where(job => string.IsNullOrWhiteSpace(query.Backend) || string.Equals(query.Backend, job.Spec.Backend, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.Queue) || string.Equals(query.Queue, ExecutionJobMetadata.ResolveQueue(job), StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.RequestedBy) || string.Equals(query.RequestedBy, job.Audit.RequestedBy, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.CorrelationId) || string.Equals(query.CorrelationId, job.Audit.CorrelationId, StringComparison.Ordinal))
                .Where(job => string.IsNullOrWhiteSpace(query.TraceId) || MatchesParameter(job, ExecutionJobParameterKeys.TraceId, query.TraceId))
                .Where(job => !query.CreatedFrom.HasValue || job.CreatedAt >= query.CreatedFrom.Value)
                .Where(job => !query.CreatedTo.HasValue || job.CreatedAt < query.CreatedTo.Value)
                .OrderByDescending(job => job.CreatedAt)
                .ToArray();
            var items = filtered.Skip((int)offset).Take(query.Limit + 1).ToArray();
            var hasMore = items.Length > query.Limit;

            return Task.FromResult(new ExecutionJobPage
            {
                Items = hasMore ? items.Take(query.Limit).ToArray() : items,
                NextCursor = hasMore ? EncodeOffset(offset + query.Limit) : null
            });
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());

        private static bool MatchesParameter(ExecutionJobRecord job, string key, string? expected)
            => job.Spec.Parameters.TryGetValue(key, out var actual) &&
               string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InMemoryLogStore : IExecutionLogStore
    {
        public int FullReads { get; private set; }

        private readonly Dictionary<string, ExecutionLogEntry[]> _logs = new(StringComparer.Ordinal);

        public void SetLogs(string operationId, params ExecutionLogEntry[] entries) => _logs[operationId] = entries;

        private readonly Dictionary<string, HashSet<string>> _appendedOnce = new(StringComparer.Ordinal);

        public Task AppendAsync(string operationId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            var current = _logs.TryGetValue(operationId, out var entries) ? entries : Array.Empty<ExecutionLogEntry>();
            _logs[operationId] = current.Append(entry).ToArray();
            return Task.CompletedTask;
        }

        public Task AppendOnceAsync(string operationId, string eventId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (!_appendedOnce.TryGetValue(operationId, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                _appendedOnce[operationId] = ids;
            }

            return ids.Add(eventId) ? AppendAsync(operationId, entry, cancellationToken) : Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(string operationId, CancellationToken cancellationToken = default)
        {
            FullReads++;
            return Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(_logs.TryGetValue(operationId, out var entries) ? entries : Array.Empty<ExecutionLogEntry>());
        }

        public Task<ExecutionLogTail> TailAsync(
            string operationId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var entries = _logs.TryGetValue(operationId, out var found) ? found : Array.Empty<ExecutionLogEntry>();
            return Task.FromResult(new ExecutionLogTail
            {
                Items = entries.TakeLast(Math.Max(1, limit)).ToArray(),
                TotalCount = entries.Length
            });
        }

        public Task<ExecutionLogPage> QueryAsync(string operationId, ExecutionLogQuery query, CancellationToken cancellationToken = default)
        {
            var offset = DecodeOffset(query.Cursor);
            var entries = _logs.TryGetValue(operationId, out var found) ? found : Array.Empty<ExecutionLogEntry>();
            var page = entries.Skip((int)offset).Take(query.Limit + 1).ToArray();
            var hasMore = page.Length > query.Limit;
            return Task.FromResult(new ExecutionLogPage
            {
                Items = hasMore ? page.Take(query.Limit).ToArray() : page,
                NextCursor = hasMore ? EncodeOffset(offset + query.Limit) : null,
                TotalCount = entries.Length
            });
        }

        public Task SetRetentionAsync(string operationId, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Removed { get; } = [];
        public List<string> Requeued { get; } = [];
        public bool ThrowOnRemove { get; set; }
        public bool ThrowOnRequeue { get; set; }

        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, IReadOnlySet<string>? acceptedRuntimeProfiles = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
        {
            Requeued.Add(operationId);
            if (ThrowOnRequeue)
            {
                throw new InvalidOperationException("requeue failed");
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
        {
            Removed.Add(operationId);
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("remove failed");
            }

            return Task.CompletedTask;
        }

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);
    }

    private sealed class RecordingBatchBackend(string backendName, bool supportsRetry) : IBatchComputeBackend
    {
        public string BackendName => backendName;

        public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.KubernetesJob;

        public int CapabilityRequests { get; private set; }

        public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            CapabilityRequests++;
            return Task.FromResult(new BatchComputeBackendCapabilities
            {
                SupportsCancellation = true,
                SupportsProgressPolling = true,
                SupportsRetry = supportsRetry
            });
        }

        public Task<BatchComputeSubmissionResult> StartAsync(
            ExecutionJobRecord job,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = "provider-" + job.OperationId,
                Message = "Started"
            });

        public Task<BatchComputeObservation> ObserveAsync(
            ExecutionJobRecord job,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                Message = job.CurrentPhase
            });

        public Task<BatchComputeObservation> CancelAsync(
            ExecutionJobRecord job,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                Message = "Cancelled"
            });
    }

    private sealed class StubArtifactStore : IArtifactStore
    {
        public Task<Artifact> CreateAsync(Artifact artifact, CancellationToken cancellationToken = default)
            => Task.FromResult(artifact);

        public Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
        {
            if (artifactId == "provider-error" || artifactId.StartsWith("https://provider-error.test/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("provider failed");
            }

            if (artifactId == "missing" || artifactId.Contains('/') || artifactId.StartsWith("data:", StringComparison.Ordinal) || artifactId.StartsWith('{'))
            {
                return Task.FromResult<Artifact?>(null);
            }

            var state = artifactId == "expired" ? ArtifactLifecycleState.Expired : ArtifactLifecycleState.Available;
            var uri = artifactId == "data-uri" ? "data:application/json;base64,eyJ4IjoxfQ==" : "https://example.test/artifacts/" + artifactId;

            return Task.FromResult<Artifact?>(new Artifact
            {
                ArtifactId = artifactId,
                Kind = ArtifactKind.File,
                Label = artifactId,
                State = state,
                Uri = uri,
                ContentType = "application/octet-stream",
                SizeBytes = 128,
                CreatedAt = DateTimeOffset.UtcNow,
                WorkspaceId = "workspace-test"
            });
        }

        public Task<IReadOnlyList<Artifact>> ListByWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Artifact>>(Array.Empty<Artifact>());

        public Task<bool> TransitionStateAsync(string artifactId, ArtifactLifecycleState newState, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> DeleteAsync(string artifactId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private static long DecodeOffset(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
            // Intentionally swallowed: a malformed base64/UTF8 cursor falls through
            // to the ArgumentException below, same as an out-of-range parsed offset.
        }

        throw new ArgumentException("Invalid cursor.");
    }

    private static string EncodeOffset(long offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));
}
