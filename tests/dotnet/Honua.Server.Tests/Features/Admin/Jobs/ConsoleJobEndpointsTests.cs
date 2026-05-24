// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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
            CreateJob("job-running-standard", ExecutionJobStatus.Running, now.AddMinutes(-1), "corr-running-standard") with
            {
                CurrentPhase = "Running",
                Spec = CreateSpec("standard-workload", new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.Queue] = "standard"
                })
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

    private static ExecutionJobSpec CreateSpec(string workloadName, IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
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
                .Where(job => string.IsNullOrWhiteSpace(query.Queue) || MatchesParameter(job, ExecutionJobParameterKeys.Queue, query.Queue))
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

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());

        private static bool MatchesParameter(ExecutionJobRecord job, string key, string? expected)
            => job.Spec.Parameters.TryGetValue(key, out var actual) &&
               string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InMemoryLogStore : IExecutionLogStore
    {
        private readonly Dictionary<string, ExecutionLogEntry[]> _logs = new(StringComparer.Ordinal);

        public void SetLogs(string operationId, params ExecutionLogEntry[] entries) => _logs[operationId] = entries;

        public Task AppendAsync(string operationId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            var current = _logs.TryGetValue(operationId, out var entries) ? entries : Array.Empty<ExecutionLogEntry>();
            _logs[operationId] = current.Append(entry).ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(_logs.TryGetValue(operationId, out var entries) ? entries : Array.Empty<ExecutionLogEntry>());

        public Task<ExecutionLogPage> QueryAsync(string operationId, ExecutionLogQuery query, CancellationToken cancellationToken = default)
        {
            var offset = DecodeOffset(query.Cursor);
            var entries = _logs.TryGetValue(operationId, out var found) ? found : Array.Empty<ExecutionLogEntry>();
            var page = entries.Skip((int)offset).Take(query.Limit + 1).ToArray();
            var hasMore = page.Length > query.Limit;
            return Task.FromResult(new ExecutionLogPage
            {
                Items = hasMore ? page.Take(query.Limit).ToArray() : page,
                NextCursor = hasMore ? EncodeOffset(offset + query.Limit) : null
            });
        }

        public Task SetRetentionAsync(string operationId, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Removed { get; } = [];
        public List<string> Requeued { get; } = [];

        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
        {
            Requeued.Add(operationId);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
        {
            Removed.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);
    }

    private sealed class StubArtifactStore : IArtifactStore
    {
        public Task<Artifact> CreateAsync(Artifact artifact, CancellationToken cancellationToken = default)
            => Task.FromResult(artifact);

        public Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
        {
            if (artifactId == "provider-error")
            {
                throw new InvalidOperationException("provider failed");
            }

            if (artifactId == "missing")
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
        }

        throw new ArgumentException("Invalid cursor.");
    }

    private static string EncodeOffset(long offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));
}
