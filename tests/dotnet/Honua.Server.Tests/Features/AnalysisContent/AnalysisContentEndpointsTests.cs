// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.AnalysisContent;
using Honua.Geoprocessing;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AnalysisContent;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AnalysisContentEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly FakeGeoprocessingJobService _jobs = new();
    private readonly FakeExecutionLogStore _logs = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AnalysisContentEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(_jobs);
                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionLogStore>(_logs);
            });
    }

    public async Task InitializeAsync()
    {
        _logs.SeedFailureLogs();
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/analysis/content/items")]
    [Endpoint("POST /api/v1/analysis/content/items/{itemId}/versions")]
    [Endpoint("GET /api/v1/analysis/content/items/{itemId}")]
    [Endpoint("GET /api/v1/analysis/content/items/{itemId}/versions/latest")]
    [Endpoint("GET /api/v1/analysis/content/items/{itemId}/versions/{contentVersion}")]
    [Endpoint("POST /api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/preview")]
    [Endpoint("GET /api/v1/analysis/artifacts/{artifactId}")]
    public async Task SavedQuery_SaveVersionReopenPreviewAndArtifactBinding_ReturnsStableContracts()
    {
        var create = new CreateAnalysisContentItemRequest
        {
            Kind = AnalysisContentKind.SavedQuery,
            Name = "incidents-by-type",
            Title = "Incidents by Type",
            SavedQuery = new SavedQueryContent
            {
                LayerId = WebAppFixture.TestLayerId,
                ServiceName = WebAppFixture.TestServiceId,
                NaturalLanguageQuery = "show incidents",
                PreviewLimit = 2,
                OutputSrid = 4326,
                Units = "meters"
            }
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/analysis/content/items", create, JsonOptions);
        var created = await ReadJsonAsync<AnalysisContentItemResponse>(createResponse, HttpStatusCode.Created);

        Assert.NotNull(created);
        Assert.Equal(1, created!.Version.Version);
        Assert.Equal(created.Item.ItemId, created.Version.ItemId);

        var createVersion = new CreateAnalysisContentVersionRequest
        {
            SavedQuery = create.SavedQuery with
            {
                NaturalLanguageQuery = "show incidents preview",
                PreviewLimit = 1
            }
        };

        var secondResponse = await _client.PostAsJsonAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions",
            createVersion,
            JsonOptions);
        var second = await ReadJsonAsync<AnalysisContentVersionResponse>(secondResponse, HttpStatusCode.Created);

        Assert.NotNull(second);
        Assert.Equal(2, second!.Version.Version);
        Assert.NotEqual(created.Version.ContentHash, second.Version.ContentHash);
        Assert.Equal(created.Version.VersionId, second.Version.BasedOnVersionId);

        var latestResponse = await _client.GetAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/latest");
        var latest = await ReadJsonAsync<AnalysisContentVersionResponse>(latestResponse, HttpStatusCode.OK);
        Assert.Equal(2, latest!.Version.Version);

        var explicitVersionResponse = await _client.GetAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/1");
        var explicitVersion = await ReadJsonAsync<AnalysisContentVersionResponse>(
            explicitVersionResponse,
            HttpStatusCode.OK);
        Assert.Equal(created.Version.VersionId, explicitVersion!.Version.VersionId);

        var itemResponse = await _client.GetAsync($"/api/v1/analysis/content/items/{created.Item.ItemId}");
        var item = await ReadJsonAsync<AnalysisContentItemResponse>(itemResponse, HttpStatusCode.OK);
        Assert.Equal(2, item!.Item.CurrentVersion);

        var previewResponse = await _client.PostAsJsonAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/2/preview",
            new PreviewSavedQueryRequest { Limit = 1 },
            JsonOptions);
        var preview = await ReadJsonAsync<SavedQueryPreviewResult>(previewResponse, HttpStatusCode.OK);

        Assert.NotNull(preview);
        Assert.Equal(created.Item.ItemId, preview!.Binding.SourceItemId);
        Assert.Equal(2, preview.Binding.SourceVersion);
        Assert.Equal("preview", preview.Binding.Role);
        Assert.NotEmpty(preview.PreviewArtifactId);
        Assert.True(preview.Features.Count <= 1);

        var artifactResponse = await _client.GetAsync($"/api/v1/analysis/artifacts/{preview.PreviewArtifactId}");
        Assert.Equal(HttpStatusCode.OK, artifactResponse.StatusCode);
        var artifact = JsonSerializer.Deserialize<AnalysisArtifactResponse>(
            await artifactResponse.Content.ReadAsStringAsync(),
            JsonOptions);
        Assert.Equal(preview.PreviewArtifactId, artifact!.Artifact.ArtifactId);
        Assert.Equal(created.Item.ItemId, artifact.Binding.SourceItemId);
        Assert.Equal("dataSource", artifact.Binding.Role);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /api/v1/analysis/content/items")]
    [Endpoint("POST /api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/runs")]
    [Endpoint("POST /api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/reruns")]
    public async Task AnalysisPackage_SubmitAndRerun_StampsVersionAndProvenanceMetadata()
    {
        var create = new CreateAnalysisContentItemRequest
        {
            Kind = AnalysisContentKind.AnalysisPackage,
            Name = "buffer-package",
            AnalysisPackage = new AnalysisPackageContent
            {
                Intent = new AnalysisIntent
                {
                    IntentId = "intent-1",
                    Goal = "buffer incidents",
                    RequestedOutputs = [ArtifactKind.FeatureLayer]
                },
                Plan = CreatePlan(),
                Parameters = new Dictionary<string, string> { ["distance"] = "10" },
                RequestedArtifacts = [ArtifactKind.FeatureLayer],
                SpatialReferenceId = 4326,
                Units = "meters"
            }
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/analysis/content/items", create, JsonOptions);
        var created = await ReadJsonAsync<AnalysisContentItemResponse>(createResponse, HttpStatusCode.Created);

        var runResponse = await _client.PostAsJsonAsync(
            $"/api/v1/analysis/content/items/{created!.Item.ItemId}/versions/1/runs",
            new RunAnalysisContentVersionRequest
            {
                IdempotencyKey = "run-1",
                Parameters = new Dictionary<string, string> { ["format"] = "geojson" }
            },
            JsonOptions);
        var run = await ReadJsonAsync<AnalysisContentJobResponse>(runResponse, HttpStatusCode.OK);

        Assert.Equal(ExecutionJobStatus.Queued, run!.Status);
        var submitted = _jobs.SubmittedJobs.Single(job => job.OperationId == run.JobId);
        Assert.Equal(created.Item.ItemId, submitted.Spec.Parameters[AnalysisContentMetadataKeys.ItemId]);
        Assert.Equal("1", submitted.Spec.Parameters[AnalysisContentMetadataKeys.Version]);
        Assert.Equal("10", submitted.Spec.Parameters["analysis.content.parameter.distance"]);
        Assert.Equal("geojson", submitted.Spec.Parameters["analysis.content.runtime_parameter.format"]);

        var rerunResponse = await _client.PostAsJsonAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/1/reruns",
            new RerunAnalysisContentVersionRequest
            {
                IdempotencyKey = "rerun-1",
                RerunOfJobId = run.JobId,
                RerunOfResultPackageId = $"{run.JobId}:v0",
                ParameterOverrides = new Dictionary<string, string> { ["distance"] = "20" }
            },
            JsonOptions);
        var rerun = await ReadJsonAsync<AnalysisContentJobResponse>(rerunResponse, HttpStatusCode.OK);

        Assert.Equal(2, rerun!.Version.Version);
        var rerunJob = _jobs.SubmittedJobs.Single(job => job.OperationId == rerun.JobId);
        Assert.Equal("2", rerunJob.Spec.Parameters[AnalysisContentMetadataKeys.Version]);
        Assert.Equal(run.JobId, rerunJob.Spec.Parameters[AnalysisContentMetadataKeys.RerunOfJobId]);
        Assert.Equal("20", rerunJob.Spec.Parameters["analysis.content.parameter.distance"]);
        Assert.Equal("20", rerunJob.Spec.Parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.distance"]);

        var rejectedRerunResponse = await _client.PostAsJsonAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/1/reruns",
            new RerunAnalysisContentVersionRequest
            {
                IdempotencyKey = "rerun-invalid",
                ParameterOverrides = new Dictionary<string, string> { ["notAPlanInput"] = "20" }
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, rejectedRerunResponse.StatusCode);
        Assert.DoesNotContain(_jobs.SubmittedJobs, job => job.OperationId == "rerun-invalid");
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/logs")]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/failure")]
    public async Task FailedJobDiagnostics_ReturnSafeClassificationAndBoundedLogs()
    {
        var failureResponse = await _client.GetAsync("/api/v1/analysis/jobs/failed-job/failure");
        var failure = await ReadJsonAsync<AnalysisJobFailure>(failureResponse, HttpStatusCode.OK);

        Assert.Equal(AnalysisJobFailureClassification.ValidationFailed, failure!.Classification);
        Assert.True(failure.IsTerminal);
        Assert.DoesNotContain("password", failure.Message, StringComparison.OrdinalIgnoreCase);

        var logsResponse = await _client.GetAsync("/api/v1/analysis/jobs/failed-job/logs?limit=1");
        var logs = await ReadJsonAsync<AnalysisJobLogs>(logsResponse, HttpStatusCode.OK);

        Assert.Equal(2, logs!.TotalCount);
        Assert.True(logs.Truncated);
        Assert.Single(logs.Entries);
        var serializedLogs = JsonSerializer.Serialize(logs, JsonOptions);
        Assert.DoesNotContain("password", serializedLogs, StringComparison.OrdinalIgnoreCase);
        // Secret-bearing metadata value under a non-sensitive key must be redacted by value, not
        // only by key filtering.
        Assert.DoesNotContain("topsecret123", serializedLogs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client_secret", serializedLogs, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _logs.TailReadCount);
        Assert.Equal(0, _logs.GetLogsReadCount);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/logs")]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/failure")]
    public async Task JobDiagnostics_ForUnknownJob_ReturnNotFound()
    {
        // Both job-scoped diagnostic endpoints must resolve the job first so an unknown id is a
        // 404, not a 200 with empty logs (logs) or a misleading conflict (failure).
        var logsResponse = await _client.GetAsync("/api/v1/analysis/jobs/missing-job/logs");
        Assert.Equal(HttpStatusCode.NotFound, logsResponse.StatusCode);
        // The log store must not be read when the job does not exist.
        Assert.Equal(0, _logs.TailReadCount);

        var failureResponse = await _client.GetAsync("/api/v1/analysis/jobs/missing-job/failure");
        Assert.Equal(HttpStatusCode.NotFound, failureResponse.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/logs")]
    public async Task JobLogs_WhenLogStoreUnavailable_ReturnsServiceUnavailable()
    {
        // The job resolves, but the Redis-backed log store is down. The documented contract is a
        // retryable 503 for an unavailable log store, not a generic 500.
        _logs.FailWithStoreOutage = true;

        var response = await _client.GetAsync("/api/v1/analysis/jobs/failed-job/logs?limit=1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected {expectedStatus}, got {response.StatusCode}. Body: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static AnalysisPlan CreatePlan()
        => new()
        {
            PlanId = "plan-1",
            IntentId = "intent-1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string> { ["distance"] = "10" }
                }
            ],
            Outputs = [ArtifactKind.FeatureLayer]
        };

    private sealed class FakeGeoprocessingJobService : IGeoprocessingJobService
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);
        private int _sequence;

        public List<ExecutionJobRecord> SubmittedJobs { get; } = [];

        public FakeGeoprocessingJobService()
        {
            var failed = CreateJob(
                "failed-job",
                ExecutionJobStatus.Failed,
                new Dictionary<string, string>(),
                "validation failed: invalid buffer distance");
            _jobs[failed.OperationId] = failed;
        }

        public void EnsureCallerAuthorized(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation)
        {
        }

        public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => new() { IsExecutable = true };

        public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => new() { EstimatedArtifacts = plan.Outputs };

        public Task<ExecutionJobRecord> SubmitJobAsync(
            AnalysisPlan plan,
            string? idempotencyKey,
            ClaimsPrincipal principal,
            IReadOnlyDictionary<string, string>? protocolMetadata = null,
            CancellationToken cancellationToken = default)
        {
            var parameters = protocolMetadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(protocolMetadata, StringComparer.Ordinal);
            for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
            {
                var step = plan.Steps[stepIndex];
                if (step.Kind != AnalysisPlanStepKind.Geoprocess)
                {
                    continue;
                }

                foreach (var input in step.Inputs)
                {
                    parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}{stepIndex}.{input.Key}"] = input.Value;
                }
            }

            var job = CreateJob(
                idempotencyKey ?? $"analysis-job-{++_sequence}",
                ExecutionJobStatus.Queued,
                parameters);
            SubmittedJobs.Add(job);
            _jobs[job.OperationId] = job;
            return Task.FromResult(job);
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => _jobs.TryGetValue(jobId, out var job)
                ? Task.FromResult(job)
                : throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static ExecutionJobRecord CreateJob(
            string jobId,
            ExecutionJobStatus status,
            IReadOnlyDictionary<string, string> parameters,
            string? errorMessage = null)
        {
            var now = DateTimeOffset.UtcNow;
            return new ExecutionJobRecord
            {
                OperationId = jobId,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = status is ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled or ExecutionJobStatus.Succeeded
                    ? now
                    : null,
                ErrorMessage = errorMessage,
                Spec = new ExecutionJobSpec
                {
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "test",
                    Kind = ExecutionJobKind.Geoprocessing,
                    WorkloadName = "test geoprocessing",
                    Parameters = parameters
                }
            };
        }
    }

    private sealed class FakeExecutionLogStore : IExecutionLogStore
    {
        private readonly Dictionary<string, List<ExecutionLogEntry>> _entries = new(StringComparer.Ordinal);

        public int GetLogsReadCount { get; private set; }

        public int TailReadCount { get; private set; }

        // When set, TailAsync simulates a Redis-backed log store outage.
        public bool FailWithStoreOutage { get; set; }

        public void SeedFailureLogs()
        {
            _entries["failed-job"] =
            [
                new ExecutionLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2),
                    Level = ExecutionLogLevel.Info,
                    Message = "validating request",
                    Phase = "validation"
                },
                new ExecutionLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1),
                    Level = ExecutionLogLevel.Error,
                    Message = "validation failed",
                    Phase = "validation",
                    Metadata = new Dictionary<string, string>
                    {
                        ["password"] = "secret",
                        ["code"] = "invalid-distance",
                        // Secret-bearing value under an otherwise innocuous key: the key-based
                        // filter does not catch it, so value-level redaction must.
                        ["detail"] = "client_secret=topsecret123"
                    }
                }
            ];
        }

        public Task AppendAsync(
            string operationId,
            ExecutionLogEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (!_entries.TryGetValue(operationId, out var entries))
            {
                entries = [];
                _entries[operationId] = entries;
            }

            entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            GetLogsReadCount++;
            return Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(
                _entries.TryGetValue(operationId, out var entries) ? entries : []);
        }

        public Task<ExecutionLogTail> TailAsync(
            string operationId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            TailReadCount++;
            if (FailWithStoreOutage)
            {
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "redis unavailable");
            }

            var entries = _entries.TryGetValue(operationId, out var found) ? found : [];
            var bounded = entries.TakeLast(Math.Max(1, limit)).ToArray();
            return Task.FromResult(new ExecutionLogTail
            {
                Items = bounded,
                TotalCount = entries.Count
            });
        }

        public Task<ExecutionLogPage> QueryAsync(
            string operationId,
            ExecutionLogQuery query,
            CancellationToken cancellationToken = default)
        {
            var offset = int.TryParse(query.Cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset)
                && parsedOffset > 0
                ? parsedOffset
                : 0;
            var limit = Math.Max(1, query.Limit);
            var entries = _entries.TryGetValue(operationId, out var found) ? found : [];
            var page = entries.Skip(offset).Take(limit + 1).ToArray();
            var hasMore = page.Length > limit;

            return Task.FromResult(new ExecutionLogPage
            {
                Items = hasMore ? page.Take(limit).ToArray() : page,
                NextCursor = hasMore ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null
            });
        }

        public Task SetRetentionAsync(
            string operationId,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
