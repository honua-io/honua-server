// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>
/// Integration tests for Console workflow package registry, lifecycle, publication,
/// and job provenance endpoints (#1185).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ProcessExecution)]
public sealed class WorkflowPackageEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly InMemoryExecutionJobStore _jobStore = new();
    private readonly InMemoryProgressStore _progressStore = new();
    private readonly RecordingJobQueue _jobQueue = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public WorkflowPackageEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IUniversalProgressStore>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IUniversalProgressStore>(_progressStore);
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
    [Endpoint("GET /api/v1/console/workflow-node-registry")]
    [Endpoint("GET /api/v1/console/workflow-node-registry/{nodeTypeId}")]
    public async Task GetRegistry_ReturnsServerOwnedProcessPalette()
    {
        var response = await _client.GetAsync("/api/v1/console/workflow-node-registry");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().NotBeNull();

        using var doc = await ReadJsonAsync(response);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("registryVersion").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("providers")[0].GetProperty("providerId").GetString()
            .Should().Be("geoprocessing.process-catalog");

        var bufferNode = data.GetProperty("nodes")
            .EnumerateArray()
            .Single(node => node.GetProperty("nodeTypeId").GetString() == "process:geometry.buffer");

        bufferNode.GetProperty("category").GetString().Should().Be("geometry");
        bufferNode.GetProperty("parameterSchemas").EnumerateArray()
            .Should().Contain(parameter => parameter.GetProperty("name").GetString() == "distance" &&
                                           parameter.GetProperty("required").GetBoolean());
        bufferNode.GetProperty("inputSchemas").EnumerateArray()
            .Should().Contain(input => input.GetProperty("name").GetString() == "wkb");
        bufferNode.GetProperty("outputSchemas").GetArrayLength().Should().BeGreaterThan(0);
        bufferNode.GetProperty("capabilityFlags").GetProperty("canDryRun").GetBoolean().Should().BeTrue();
        bufferNode.GetProperty("capabilityFlags").GetProperty("supportsProcessEndpoint").GetBoolean().Should().BeTrue();
        bufferNode.GetProperty("runtimeKind").GetString().Should().Be("Geoprocessing");
        bufferNode.GetProperty("runtimeHints").GetProperty("workerProfile").GetString().Should().Be("geoprocessing");
        bufferNode.GetProperty("eligibilityWarnings").GetArrayLength().Should().Be(0);

        var nodeResponse = await _client.GetAsync("/api/v1/console/workflow-node-registry/process%3Ageometry.buffer");
        nodeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var nodeDoc = await ReadJsonAsync(nodeResponse);
        nodeDoc.RootElement.GetProperty("data").GetProperty("nodeTypeId").GetString()
            .Should().Be("process:geometry.buffer");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/workflow-packages")]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}")]
    [Endpoint("PUT /api/v1/console/workflow-packages/{packageId}")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}/versions")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/validate")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/dry-run")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    [Endpoint("GET /api/v1/console/workflow-publications")]
    [Endpoint("POST /api/v1/console/workflow-publications/{publicationId}/runs")]
    public async Task WorkflowLifecycle_ValidatesDryRunsPublishesTargetsAndStampsJobProvenance()
    {
        var packageId = await CreatePackageAsync("area-lifecycle", CreateAreaGraph());

        var listPackages = await _client.GetAsync("/api/v1/console/workflow-packages");
        listPackages.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var listPackagesDoc = await ReadJsonAsync(listPackages))
        {
            listPackagesDoc.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
                .Should().Contain(item => item.GetProperty("packageId").GetString() == packageId);
        }

        var getPackage = await _client.GetAsync($"/api/v1/console/workflow-packages/{packageId}");
        getPackage.StatusCode.Should().Be(HttpStatusCode.OK);

        var update = CreatePackageRequest("area-lifecycle-updated", CreateAreaGraph(), packageId);
        var updateResponse = await _client.PutAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}",
            update,
            JsonOptions);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var version = await CreateVersionAsync(packageId);
        version.PackageHash.Should().NotBeNullOrWhiteSpace();

        var listVersions = await _client.GetAsync($"/api/v1/console/workflow-packages/{packageId}/versions");
        listVersions.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var listVersionsDoc = await ReadJsonAsync(listVersions))
        {
            listVersionsDoc.RootElement.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(1);
        }

        var getVersion = await _client.GetAsync($"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}");
        getVersion.StatusCode.Should().Be(HttpStatusCode.OK);

        var validate = await _client.PostAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/validate",
            null);
        validate.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var validateDoc = await ReadJsonAsync(validate))
        {
            validateDoc.RootElement.GetProperty("data").GetProperty("isValid").GetBoolean().Should().BeTrue();
        }

        var dryRun = await _client.PostAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/dry-run",
            null);
        dryRun.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var dryRunDoc = await ReadJsonAsync(dryRun))
        {
            var data = dryRunDoc.RootElement.GetProperty("data");
            data.GetProperty("validation").GetProperty("isValid").GetBoolean().Should().BeTrue();
            data.GetProperty("estimatedDurationSeconds").GetInt32().Should().BeGreaterThan(0);
            data.GetProperty("estimatedCostWeight").GetInt32().Should().BeGreaterThan(0);
            data.GetProperty("outputSchemas").GetArrayLength().Should().BeGreaterThan(0);
            data.GetProperty("packageHash").GetString().Should().Be(version.PackageHash);
        }

        await PublishAsync(packageId, version.Version, new
        {
            publicationId = "pub-job-1185",
            target = "Job",
            enabled = true
        });
        await PublishAsync(packageId, version.Version, new
        {
            publicationId = "pub-schedule-1185",
            target = "Schedule",
            schedule = new
            {
                cronExpression = "0 0 * * *",
                timeZone = "UTC",
                enabled = true
            },
            enabled = true
        });
        await PublishAsync(packageId, version.Version, new
        {
            publicationId = "pub-process-1185",
            target = "ProcessEndpoint",
            processId = "workflow.tests.area",
            enabled = true
        });

        var publications = await _client.GetAsync("/api/v1/console/workflow-publications");
        publications.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var publicationsDoc = await ReadJsonAsync(publications))
        {
            var items = publicationsDoc.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
            items.Should().Contain(item => item.GetProperty("publicationId").GetString() == "pub-job-1185" &&
                                           item.GetProperty("target").GetString() == "Job");
            items.Should().Contain(item => item.GetProperty("publicationId").GetString() == "pub-schedule-1185" &&
                                           item.GetProperty("target").GetString() == "Schedule");
            items.Should().Contain(item => item.GetProperty("publicationId").GetString() == "pub-process-1185" &&
                                           item.GetProperty("target").GetString() == "ProcessEndpoint" &&
                                           item.GetProperty("processId").GetString() == "workflow.tests.area");
        }

        var run = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-publications/pub-job-1185/runs",
            new { idempotencyKey = "workflow-package-run-1185" },
            JsonOptions);
        var runBody = await run.Content.ReadAsStringAsync();
        run.StatusCode.Should().Be(HttpStatusCode.Created, runBody);
        using var runDoc = JsonDocument.Parse(runBody);
        var runData = runDoc.RootElement.GetProperty("data");
        var jobId = runData.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        runData.GetProperty("packageId").GetString().Should().Be(packageId);
        runData.GetProperty("packageVersion").GetInt32().Should().Be(version.Version);
        runData.GetProperty("provenance").GetProperty("workflow.packageHash").GetString().Should().Be(version.PackageHash);

        var job = await _jobStore.GetAsync(jobId!);
        job.Should().NotBeNull();
        job!.Spec.Parameters.Should().Contain("workflow.packageId", packageId);
        job.Spec.Parameters.Should().Contain("workflow.packageVersion", version.Version.ToString(CultureInfo.InvariantCulture));
        job.Spec.Parameters.Should().Contain("workflow.publicationId", "pub-job-1185");
        job.Spec.Parameters.Should().Contain("workflow.packageHash", version.PackageHash);
        job.Spec.Parameters.Should().Contain("workflow.publicationTarget", "Job");
        job.Spec.Parameters.Should().Contain("honua.geoprocessing.process_definitions", "geometry.area");
        _jobQueue.Enqueued.Should().Contain(job.OperationId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions")]
    public async Task CreateVersion_WithMissingRequiredParameter_ReturnsValidationFailure()
    {
        var packageId = await CreatePackageAsync(
            "invalid-area",
            CreateAreaGraph(new Dictionary<string, string>
            {
                ["wkb"] = "AQ=="
            }));

        var response = await _client.PostAsync($"/api/v1/console/workflow-packages/{packageId}/versions", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = await ReadJsonAsync(response);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("data").GetProperty("isValid").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("data").GetProperty("failures").EnumerateArray()
            .Should().Contain(failure => failure.GetProperty("code").GetString() == "MISSING_REQUIRED_PARAMETER" &&
                                         failure.GetProperty("fieldPath").GetString()!.EndsWith(".srid", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task PublishVersion_WithDuplicatePublicationId_ReturnsConflict()
    {
        var packageId = await CreatePackageAsync("duplicate-publication", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);

        await PublishAsync(packageId, version.Version, new
        {
            publicationId = "pub-duplicate-1185",
            target = "Job",
            enabled = true
        });

        var conflict = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/publish",
            new
            {
                publicationId = "pub-duplicate-1185",
                target = "Job",
                enabled = true
            },
            JsonOptions);

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-publications/{publicationId}/runs")]
    public async Task RunPublication_WithReservedProvenanceOverride_PreservesStampedProvenance()
    {
        var packageId = await CreatePackageAsync("provenance-guard", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        await PublishAsync(packageId, version.Version, new
        {
            publicationId = "pub-provenance-1185",
            target = "Job",
            enabled = true
        });

        var run = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-publications/pub-provenance-1185/runs",
            new
            {
                idempotencyKey = "provenance-guard-run",
                parameters = new Dictionary<string, string>
                {
                    // Attempt to spoof reserved provenance; the server must ignore these.
                    ["workflow.packageId"] = "spoofed-package",
                    ["workflow.packageHash"] = "spoofed-hash",
                    ["analysis.region"] = "pacific"
                }
            },
            JsonOptions);

        var runBody = await run.Content.ReadAsStringAsync();
        run.StatusCode.Should().Be(HttpStatusCode.Created, runBody);

        using var runDoc = JsonDocument.Parse(runBody);
        var runData = runDoc.RootElement.GetProperty("data");
        var jobId = runData.GetProperty("jobId").GetString();
        var provenance = runData.GetProperty("provenance");
        provenance.GetProperty("workflow.packageId").GetString().Should().Be(packageId);
        provenance.GetProperty("workflow.packageHash").GetString().Should().Be(version.PackageHash);
        provenance.GetProperty("analysis.region").GetString().Should().Be("pacific");

        var job = await _jobStore.GetAsync(jobId!);
        job.Should().NotBeNull();
        job!.Spec.Parameters.Should().Contain("workflow.packageId", packageId);
        job.Spec.Parameters.Should().Contain("workflow.packageHash", version.PackageHash);
        job.Spec.Parameters.Should().Contain("analysis.region", "pacific");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/dry-run")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task DataWiredWorkflow_ValidatesAndPublishesToScheduleButRejectsSingleJobTargets()
    {
        var packageId = await CreatePackageAsync("data-wired", CreateWiredGraph());

        // The downstream node's required `wkb` input is supplied by a data edge, so an immutable
        // version can be created without a literal value (the wiring satisfies the requirement).
        var version = await CreateVersionAsync(packageId);
        version.PackageHash.Should().NotBeNullOrWhiteSpace();

        var dryRun = await _client.PostAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/dry-run",
            null);
        dryRun.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var dryRunDoc = await ReadJsonAsync(dryRun))
        {
            var data = dryRunDoc.RootElement.GetProperty("data");
            data.GetProperty("validation").GetProperty("isValid").GetBoolean().Should().BeTrue();
            data.GetProperty("artifacts").GetArrayLength().Should().BeGreaterThan(0);
        }

        // Job and process-endpoint targets compile to a single dispatched job and cannot resolve
        // cross-node data bindings, so publishing a data-wired graph to them is rejected.
        foreach (var target in new[] { "Job", "ProcessEndpoint" })
        {
            var rejected = await _client.PostAsJsonAsync(
                $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/publish",
                new { publicationId = $"pub-wired-{target}", target, processId = "workflow.tests.wired", enabled = true },
                JsonOptions);

            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var doc = await ReadJsonAsync(rejected);
            doc.RootElement.GetProperty("data").GetProperty("failures").EnumerateArray()
                .Should().Contain(failure => failure.GetProperty("code").GetString() == "UNSUPPORTED_DATA_BINDING_TARGET");
        }

        // The orchestration-backed schedule target accepts data-wired graphs.
        await PublishAsync(packageId, version.Version, new
        {
            publicationId = "pub-wired-schedule",
            target = "Schedule",
            schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true },
            enabled = true
        });
    }

    private async Task<string> CreatePackageAsync(string name, object graph)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest(name, graph),
            JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("data").GetProperty("packageId").GetString()!;
    }

    private async Task<(int Version, string PackageHash)> CreateVersionAsync(string packageId)
    {
        var response = await _client.PostAsync($"/api/v1/console/workflow-packages/{packageId}/versions", null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = await ReadJsonAsync(response);
        var data = doc.RootElement.GetProperty("data");
        return (data.GetProperty("version").GetInt32(), data.GetProperty("packageHash").GetString()!);
    }

    private async Task PublishAsync(string packageId, int version, object request)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version}/publish",
            request,
            JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static object CreatePackageRequest(string name, object graph, string? packageId = null)
        => new
        {
            packageId,
            name,
            description = "Console workflow package integration test.",
            @namespace = "tests.workflow-packages",
            graph,
            metadata = new Dictionary<string, string>
            {
                ["test"] = "1185"
            }
        };

    private static object CreateAreaGraph(IReadOnlyDictionary<string, string>? parameters = null)
        => new
        {
            nodes = new[]
            {
                new
                {
                    nodeId = "area-1",
                    nodeTypeId = "process:geometry.area",
                    label = "Area",
                    parameters = parameters ?? new Dictionary<string, string>
                    {
                        ["wkb"] = "AQ==",
                        ["srid"] = "4326"
                    }
                }
            },
            edges = Array.Empty<object>(),
            metadata = new Dictionary<string, string>()
        };

    private static object CreateWiredGraph()
        => new
        {
            nodes = new object[]
            {
                new
                {
                    nodeId = "source",
                    nodeTypeId = "process:geometry.area",
                    parameters = new Dictionary<string, string>
                    {
                        ["wkb"] = "AQ==",
                        ["srid"] = "4326"
                    }
                },
                new
                {
                    nodeId = "sink",
                    nodeTypeId = "process:geometry.area",
                    parameters = new Dictionary<string, string>
                    {
                        // `wkb` is intentionally omitted; it is wired from the source node below.
                        ["srid"] = "4326"
                    }
                }
            },
            edges = new object[]
            {
                new
                {
                    sourceNodeId = "source",
                    targetNodeId = "sink",
                    targetPort = "wkb"
                }
            },
            metadata = new Dictionary<string, string>()
        };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed class InMemoryExecutionJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(
            ExecutionJobRecord job,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
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
            var items = _jobs.Values
                .Where(job => query.Statuses.Count == 0 || query.Statuses.Contains(job.Status))
                .Where(job => !query.Kind.HasValue || job.Spec.Kind == query.Kind.Value)
                .OrderByDescending(job => job.CreatedAt)
                .Take(query.Limit)
                .ToArray();

            return Task.FromResult(new ExecutionJobPage
            {
                Items = items,
                NextCursor = null
            });
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
            ExecutionJobKind? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values
                .Where(job => !kind.HasValue || job.Spec.Kind == kind.Value)
                .ToArray());
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Enqueued { get; } = [];

        public Task EnqueueAsync(
            string operationId,
            OperationPriority priority = OperationPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            Enqueued.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<string?> TryClaimAsync(
            string workerId,
            IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
            IReadOnlySet<string>? acceptedRuntimeProfiles = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(
            string operationId,
            OperationPriority priority = OperationPriority.Normal,
            TimeSpan? visibleAfter = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);
    }

    private sealed class InMemoryProgressStore : IUniversalProgressStore
    {
        private readonly Dictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

        public Task SetProgressAsync(
            string operationId,
            IOperationProgress progress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(
            string operationId,
            CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_progress.TryGetValue(operationId, out var progress) ? progress as TProgress : null);

        public Task<IOperationProgress?> GetProgressAsync(
            string operationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_progress.TryGetValue(operationId, out var progress) ? progress : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _progress.Remove(operationId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(
            OperationType? operationType = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_progress
                .Where(entry => !operationType.HasValue || entry.Value.Type == operationType.Value)
                .Select(entry => entry.Key)
                .ToArray());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(
            OperationType operationType,
            CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(_progress.Values
                .Where(progress => progress.Type == operationType)
                .OfType<TProgress>()
                .ToArray());
    }
}
