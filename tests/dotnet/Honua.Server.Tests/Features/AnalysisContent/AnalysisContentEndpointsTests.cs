// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        var created = await PostAndReadAsync<AnalysisContentItemResponse>(
            "/api/v1/analysis/content/items",
            create,
            HttpStatusCode.Created);

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

        var second = await PostAndReadAsync<AnalysisContentVersionResponse>(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions",
            createVersion,
            HttpStatusCode.Created);

        Assert.NotNull(second);
        Assert.Equal(2, second!.Version.Version);
        Assert.NotEqual(created.Version.ContentHash, second.Version.ContentHash);
        Assert.Equal(created.Version.VersionId, second.Version.BasedOnVersionId);

        var latest = await ReadAsync<AnalysisContentVersionResponse>(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/latest");
        Assert.Equal(2, latest!.Version.Version);

        var explicitVersion = await ReadAsync<AnalysisContentVersionResponse>(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/1");
        Assert.Equal(created.Version.VersionId, explicitVersion!.Version.VersionId);

        var item = await ReadAsync<AnalysisContentItemResponse>(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}");
        Assert.Equal(2, item!.Item.CurrentVersion);

        var preview = await PostAndReadAsync<SavedQueryPreviewResult>(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/2/preview",
            new PreviewSavedQueryRequest { Limit = 1 },
            HttpStatusCode.OK);

        Assert.NotNull(preview);
        Assert.Equal(created.Item.ItemId, preview!.Binding.SourceItemId);
        Assert.Equal(2, preview.Binding.SourceVersion);
        Assert.Equal("preview", preview.Binding.Role);
        Assert.NotEmpty(preview.PreviewArtifactId);
        Assert.True(preview.Features.Count <= 1);

        var artifact = await ReadAsync<AnalysisArtifactResponse>(
            $"/api/v1/analysis/artifacts/{preview.PreviewArtifactId}");
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

        var created = await PostAndReadAsync<AnalysisContentItemResponse>(
            "/api/v1/analysis/content/items",
            create,
            HttpStatusCode.Created);

        var run = await PostAndReadAsync<AnalysisContentJobResponse>(
            $"/api/v1/analysis/content/items/{created!.Item.ItemId}/versions/1/runs",
            new RunAnalysisContentVersionRequest
            {
                IdempotencyKey = "run-1",
                Parameters = new Dictionary<string, string> { ["format"] = "geojson" }
            },
            HttpStatusCode.OK);

        Assert.Equal(ExecutionJobStatus.Queued, run!.Status);
        var submitted = _jobs.SubmittedJobs.Single(job => job.OperationId == run.JobId);
        Assert.Equal(created.Item.ItemId, submitted.Spec.Parameters[AnalysisContentMetadataKeys.ItemId]);
        Assert.Equal("1", submitted.Spec.Parameters[AnalysisContentMetadataKeys.Version]);
        Assert.Equal("10", submitted.Spec.Parameters["analysis.content.parameter.distance"]);
        Assert.Equal("geojson", submitted.Spec.Parameters["analysis.content.runtime_parameter.format"]);

        var rerun = await PostAndReadAsync<AnalysisContentJobResponse>(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/1/reruns",
            new RerunAnalysisContentVersionRequest
            {
                IdempotencyKey = "rerun-1",
                RerunOfJobId = run.JobId,
                RerunOfResultPackageId = $"{run.JobId}:v0",
                ParameterOverrides = new Dictionary<string, string> { ["distance"] = "20" }
            },
            HttpStatusCode.OK);

        Assert.Equal(2, rerun!.Version.Version);
        var rerunJob = _jobs.SubmittedJobs.Single(job => job.OperationId == rerun.JobId);
        Assert.Equal("2", rerunJob.Spec.Parameters[AnalysisContentMetadataKeys.Version]);
        Assert.Equal(run.JobId, rerunJob.Spec.Parameters[AnalysisContentMetadataKeys.RerunOfJobId]);
        Assert.Equal("20", rerunJob.Spec.Parameters["analysis.content.parameter.distance"]);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/logs")]
    [Endpoint("GET /api/v1/analysis/jobs/{jobId}/failure")]
    public async Task FailedJobDiagnostics_ReturnSafeClassificationAndBoundedLogs()
    {
        var failure = await ReadAsync<AnalysisJobFailure>("/api/v1/analysis/jobs/failed-job/failure");

        Assert.Equal(AnalysisJobFailureClassification.ValidationFailed, failure!.Classification);
        Assert.True(failure.IsTerminal);
        Assert.DoesNotContain("password", failure.Message, StringComparison.OrdinalIgnoreCase);

        var logs = await ReadAsync<AnalysisJobLogs>("/api/v1/analysis/jobs/failed-job/logs?limit=1");

        Assert.Equal(2, logs!.TotalCount);
        Assert.True(logs.Truncated);
        Assert.Single(logs.Entries);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(logs, JsonOptions), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T?> ReadAsync<T>(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    private async Task<T?> PostAndReadAsync<T>(string path, object payload, HttpStatusCode expectedStatus)
    {
        var response = await _client.PostAsJsonAsync(path, payload, JsonOptions);
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
            var job = CreateJob(
                idempotencyKey ?? $"analysis-job-{++_sequence}",
                ExecutionJobStatus.Queued,
                protocolMetadata ?? new Dictionary<string, string>());
            SubmittedJobs.Add(job);
            _jobs[job.OperationId] = job;
            return Task.FromResult(job);
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs[jobId]);

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
                        ["code"] = "invalid-distance"
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
            => Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(
                _entries.TryGetValue(operationId, out var entries) ? entries : []);

        public Task SetRetentionAsync(
            string operationId,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
