// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Endpoint round-trip tests for the Esri-conventional task-name alias overlay
/// (<see cref="Honua.Protocols.GeoServices.GPServer.GPServerEsriTaskAliases"/>):
/// task-list publication, task-info/submitJob/execute resolution via alias,
/// unknown-alias behavior, deliberate case-insensitivity, duplicate-name handling,
/// and the deterministic collision policy (a real catalog process ID always wins
/// over an alias; aliases only resolve when no real process owns the name).
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerEsriTaskAliasEndpointTests : IAsyncLifetime
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ServiceId = WebAppFixture.TestServiceId;

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Task list (service info)
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_TaskList_PublishesInternalIdAndEsriAliasWithoutDuplicates()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        // Both addressing forms are published for an aliased process...
        tasks.Should().Contain("geometry.buffer");
        tasks.Should().Contain("Buffer");
        // ...a non-aliased Honua-specific process keeps only its internal-ID name...
        tasks.Should().Contain("analytics.cluster");
        // ...and no task name is ever published twice (duplicate-name handling).
        tasks.Should().OnlyHaveUniqueItems();
    }

    // -----------------------------------------------------------------------
    // Task info via alias
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_ByEsriAlias_EchoesAliasAndPublishesCanonicalParameters()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer/Buffer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // name echoes the address used; the definition is geometry.buffer's.
        root.GetProperty("name").GetString().Should().Be("Buffer");
        root.GetProperty("displayName").GetString().Should().Be("Buffer");
        root.GetProperty("description").GetString().Should().Contain("Creates a polygon");

        // CONTRACT HONESTY (#2788): the alias renames the task, it does not adapt the
        // wire contract to Esri Buffer's. Task-info must publish the canonical Honua
        // inputs (base64 WKB + SRID + distance) — not Esri Buffer's feature-record-set
        // input or linear-unit distance — so a client driving invocation from this
        // metadata sends the inputs the engine actually expects.
        var inputs = root.GetProperty("parameters").EnumerateArray()
            .Where(p => p.GetProperty("direction").GetString() == "esriGPParameterDirectionInput")
            .ToArray();

        inputs.Should().Contain(parameter =>
            parameter.GetProperty("name").GetString() == "wkb" &&
            parameter.GetProperty("description").GetString()!.Contains("base64-encoded WKB", StringComparison.Ordinal));
        inputs.Should().Contain(parameter =>
            parameter.GetProperty("name").GetString() == "srid");
        inputs.Should().Contain(parameter =>
            parameter.GetProperty("name").GetString() == "distance" &&
            parameter.GetProperty("dataType").GetString() == "GPDouble");
        inputs.Should().NotContain(parameter =>
            parameter.GetProperty("dataType").GetString() == "GPFeatureRecordSetLayer",
            "the aliased task must not pretend to accept Esri Buffer's feature-record-set input");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_ByAliasCaseVariant_ResolvesCaseInsensitively()
    {
        // DELIBERATE: alias resolution is case-insensitive (ArcGIS client tooling is
        // inconsistent about task-name casing). This pins that behavior; the echoed
        // name mirrors the exact address the caller used.
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer/BUFFER");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be("BUFFER");
        root.GetProperty("displayName").GetString().Should().Be("Buffer");
        root.GetProperty("description").GetString().Should().Contain("Creates a polygon");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_ByInternalProcessIdCaseVariant_ReturnsNotFound()
    {
        // DELIBERATE ASYMMETRY: internal process IDs stay case-sensitive (the
        // ESTABLISHED ordinal catalog contract); only the alias overlay is
        // case-insensitive. GEOMETRY.BUFFER is neither an exact process ID nor an
        // alias, so it must 404 rather than fuzzy-match.
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer/GEOMETRY.BUFFER");

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_UnknownAlias_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer/NotARealEsriToolName");

        await response.AssertGeoServicesErrorAsync(404);
    }

    // -----------------------------------------------------------------------
    // submitJob via alias
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ByEsriAlias_ResolvesToCanonicalProcess()
    {
        var recordingService = new RecordingJobService();
        var submitFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(recordingService);
            });

        await submitFixture.InitializeAsync();
        try
        {
            using var client = submitFixture.CreateAdminClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "25.5"
            });

            var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/Buffer/submitJob", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("jobId").GetString().Should().Be("gp-alias-job");
            doc.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");

            // The alias resolves to the canonical engine process...
            recordingService.LastPlan.Should().NotBeNull();
            recordingService.LastPlan!.Steps.Should().ContainSingle()
                .Which.ProcessId.Should().Be("geometry.buffer");
            // ...while the protocol binding metadata keeps the addressed task name so
            // job-status/results/cancel round-trips under the same alias route.
            recordingService.LastProtocolMetadata.Should().Contain(
                new KeyValuePair<string, string>("gpserver.taskName", "Buffer"));
        }
        finally
        {
            await submitFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnknownAlias_ReturnsNotFound()
    {
        using var client = _fixture.CreateAdminClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/NotARealEsriToolName/submitJob", content);

        await response.AssertGeoServicesErrorAsync(404);
    }

    // -----------------------------------------------------------------------
    // execute via alias
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task Execute_ByEsriAlias_RunsCanonicalProcessInline()
    {
        var executeFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new SyncExecuteJobService());
            });

        await executeFixture.InitializeAsync();
        try
        {
            using var client = executeFixture.CreateAdminClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "10"
            });

            var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/Buffer/execute", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded");
            root.GetProperty("results").EnumerateArray().Should().NotBeEmpty(
                "geometry.buffer is sync-eligible, so its alias must execute inline too");
        }
        finally
        {
            await executeFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task Execute_UnknownAlias_ReturnsNotFound()
    {
        using var client = _fixture.CreateAdminClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/NotARealEsriToolName/execute", content);

        await response.AssertGeoServicesErrorAsync(404);
    }

    // -----------------------------------------------------------------------
    // Collision policy: a real catalog process ID always wins over an alias
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_CatalogProcessIdMatchingAlias_PublishesNameOnlyOnce()
    {
        var collisionFixture = CreateCollisionFixture();

        await collisionFixture.InitializeAsync();
        try
        {
            var response = await collisionFixture.Client.GetAsync($"/rest/services/{ServiceId}/GPServer");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();

            // The custom process owns the name; the geometry.buffer alias is suppressed
            // so "Buffer" is published exactly once with exactly one meaning.
            tasks.Count(name => name == "Buffer").Should().Be(1);
            tasks.Should().Contain("geometry.buffer");
            tasks.Should().OnlyHaveUniqueItems();
        }
        finally
        {
            await collisionFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_CatalogProcessIdMatchingAlias_ResolvesToCatalogProcess()
    {
        var collisionFixture = CreateCollisionFixture();

        await collisionFixture.InitializeAsync();
        try
        {
            var response = await collisionFixture.Client.GetAsync($"/rest/services/{ServiceId}/GPServer/Buffer");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            // Deterministic: the real process named "Buffer" wins over the alias, every time.
            root.GetProperty("name").GetString().Should().Be("Buffer");
            root.GetProperty("displayName").GetString().Should().Be("Custom Buffer");
            root.GetProperty("description").GetString().Should().Be(CustomBufferDescription);
        }
        finally
        {
            await collisionFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_AliasShadowedByCatalogProcessCaseVariant_ReturnsNotFound()
    {
        // A catalog process named "Buffer" owns EVERY casing of the name: "buffer" is
        // not an exact (ordinal) process-ID match, and the case-insensitive alias
        // overlay is bypassed because a real process holds the name — so the request
        // 404s instead of nondeterministically routing to geometry.buffer.
        var collisionFixture = CreateCollisionFixture();

        await collisionFixture.InitializeAsync();
        try
        {
            var response = await collisionFixture.Client.GetAsync($"/rest/services/{ServiceId}/GPServer/buffer");

            await response.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await collisionFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CatalogProcessIdMatchingAlias_SubmitsCatalogProcess()
    {
        var recordingService = new RecordingJobService();
        var collisionFixture = CreateCollisionFixture(services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton<IGeoprocessingJobService>(recordingService);
        });

        await collisionFixture.InitializeAsync();
        try
        {
            using var client = collisionFixture.CreateAdminClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["input"] = "value"
            });

            var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/Buffer/submitJob", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("jobId").GetString().Should().Be("gp-alias-job");

            recordingService.LastPlan.Should().NotBeNull();
            recordingService.LastPlan!.Steps.Should().ContainSingle()
                .Which.ProcessId.Should().Be("Buffer",
                    "the real catalog process named 'Buffer' must win over the geometry.buffer alias");
        }
        finally
        {
            await collisionFixture.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Fixtures and test doubles
    // -----------------------------------------------------------------------

    private const string CustomBufferDescription =
        "Catalog process that owns the name 'Buffer', shadowing the geometry.buffer alias.";

    /// <summary>
    /// Builds a fixture whose process catalog contains a real process whose ID collides
    /// with the Esri alias <c>Buffer</c>, to prove the deterministic collision policy.
    /// </summary>
    private static WebAppFixture CreateCollisionFixture(Action<IServiceCollection>? extraServices = null)
        => new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IProcessCatalog>();
                services.AddSingleton<IProcessCatalog>(
                    new AliasCollidingProcessCatalog(new BuiltInProcessCatalog()));
                extraServices?.Invoke(services);
            });

    /// <summary>
    /// Wraps the built-in catalog and adds a process whose ID equals the Esri alias
    /// <c>Buffer</c>, simulating a custom/catalog process colliding with the alias table.
    /// </summary>
    private sealed class AliasCollidingProcessCatalog(IProcessCatalog inner) : IProcessCatalog
    {
        private static readonly ProcessDefinition CustomBuffer = new()
        {
            ProcessId = "Buffer",
            Title = "Custom Buffer",
            Description = CustomBufferDescription,
            Category = "custom",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "input",
                    DisplayName = "Input",
                    Description = "Custom input value.",
                    ValueType = ProcessParameterValueType.Text,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        };

        public ProcessDefinition? GetProcess(string processId)
            => processId == CustomBuffer.ProcessId ? CustomBuffer : inner.GetProcess(processId);

        public IReadOnlyList<ProcessDefinition> ListProcesses()
            => [.. inner.ListProcesses(), CustomBuffer];

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
            => category == CustomBuffer.Category ? [CustomBuffer] : inner.GetProcessesByCategory(category);
    }

    /// <summary>
    /// Records the submitted plan/metadata so tests can assert which canonical process
    /// an alias-addressed submitJob resolved to.
    /// </summary>
    private sealed class RecordingJobService : IGeoprocessingJobService
    {
        public AnalysisPlan? LastPlan { get; private set; }

        public IReadOnlyDictionary<string, string>? LastProtocolMetadata { get; private set; }

        public Task EnsureCallerAuthorizedAsync(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> SubmitJobAsync(
            AnalysisPlan plan,
            string? idempotencyKey,
            ClaimsPrincipal principal,
            IReadOnlyDictionary<string, string>? protocolMetadata = null,
            CancellationToken cancellationToken = default)
        {
            LastPlan = plan;
            LastProtocolMetadata = protocolMetadata;

            return Task.FromResult(CreateJobRecord("gp-alias-job", ExecutionJobStatus.Queued, protocolMetadata));
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoprocessingJobListPage { Items = Array.Empty<ExecutionJobRecord>() });

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Test double for the synchronous /execute path: submission returns a terminal
    /// Succeeded job immediately so the polling loop completes on the first tick.
    /// </summary>
    private sealed class SyncExecuteJobService : IGeoprocessingJobService
    {
        private static readonly AnalysisResultPackage SuccessPackage =
            AnalysisResultPackage.CreateCompleted(
                resultPackageId: "pkg-gpserver-alias-execute",
                summary: new ResultSummary { Title = "Alias GP execute" },
                artifacts:
                [
                    new ArtifactRef
                    {
                        ArtifactId = "art-alias-execute-1",
                        Kind = ArtifactKind.FeatureLayer,
                        Label = "Buffered Output",
                        Uri = "https://example.test/artifacts/alias-execute.geojson"
                    }
                ],
                workspaceRefs: [],
                provenance: new ProvenanceRecord
                {
                    Sources = [],
                    ProcessDefinitions = ["geometry.buffer"],
                    ExecutedAt = DateTimeOffset.UtcNow
                });

        public Task EnsureCallerAuthorizedAsync(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> SubmitJobAsync(
            AnalysisPlan plan,
            string? idempotencyKey,
            ClaimsPrincipal principal,
            IReadOnlyDictionary<string, string>? protocolMetadata = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateJobRecord(
                $"gp-alias-sync-{Guid.NewGuid():N}", ExecutionJobStatus.Succeeded, protocolMetadata));

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateJobRecord(jobId, ExecutionJobStatus.Succeeded, null));

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoprocessingJobListPage { Items = Array.Empty<ExecutionJobRecord>() });

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SuccessPackage);

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static ExecutionJobRecord CreateJobRecord(
        string jobId,
        ExecutionJobStatus status,
        IReadOnlyDictionary<string, string>? protocolMetadata)
        => new()
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = status == ExecutionJobStatus.Succeeded ? DateTimeOffset.UtcNow : null,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "gp-alias-tests",
                Parameters = protocolMetadata != null
                    ? new Dictionary<string, string>(protocolMetadata)
                    : new Dictionary<string, string>()
            }
        };
}
