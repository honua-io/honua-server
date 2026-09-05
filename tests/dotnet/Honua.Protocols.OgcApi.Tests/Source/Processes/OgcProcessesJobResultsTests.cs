// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Integration coverage for OGC API Processes result evidence.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesJobResultsTests : IClassFixture<OgcProcessesJobResultsTestsFixture>
{
    private const string JobId = "ogc-gp-result-job";
    private const string SelectedJobId = "ogc-gp-selected-result-job";
    private const string ValueJobId = "ogc-gp-value-result-job";
    private const string RawJobId = "ogc-gp-raw-result-job";
    private const string MultiRawJobId = "ogc-gp-multi-raw-result-job";
    private const string CanonicalJobId = "ogc-gp-canonical-result-job";

    private readonly WebAppFixture _fixture;

    public OgcProcessesJobResultsTests(OgcProcessesJobResultsTestsFixture fixture) => _fixture = fixture.App;

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_SucceededArtifactBackedJob_ExposesResultsLink()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var links = doc.RootElement.GetProperty("links").EnumerateArray();
        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/results" &&
            link.GetProperty("href").GetString()!.EndsWith($"/ogc/processes/jobs/{JobId}/results", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_SucceededArtifactBackedJob_ReturnsNonEmptyResultEvidence()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{JobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBe("{}");

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.EnumerateObject().Should().NotBeEmpty();
        var output = doc.RootElement.GetProperty("outputFeatureLayer");
        output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
        output.GetProperty("href").GetString().Should().Be("https://example.test/ogc-buffer-output.geojson");
        output.GetProperty("id").GetString().Should().Be("artifact-output-1");

        var duplicateOutput = doc.RootElement.GetProperty("outputFeatureLayer_2");
        duplicateOutput.GetProperty("kind").GetString().Should().Be("Report");
        duplicateOutput.GetProperty("href").GetString().Should().Be("https://example.test/ogc-buffer-output-summary.json");
        duplicateOutput.GetProperty("id").GetString().Should().Be("artifact-output-2");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_ExplicitNonLeadingSelection_ReturnsOnlySelectedOutput()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{SelectedJobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("outputTable");
        doc.RootElement.GetProperty("outputTable").GetProperty("id").GetString()
            .Should().Be("selected-artifact-2");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_AdvertisedValueTransmission_ReturnsInlineQualifiedValue()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{ValueJobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var output = doc.RootElement.GetProperty("outputFeatureLayer");
        output.TryGetProperty("href", out _).Should().BeFalse(
            "a process advertising only value transmission must not return a reference object (#4144)");
        output.GetProperty("mediaType").GetString().Should().Be("application/geo+json");
        output.GetProperty("value").GetProperty("type").GetString().Should().Be("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_AsyncRawRequest_ReturnsNativeRepresentation()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{RawJobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"value\":42}",
            "the persisted raw response choice applies when an asynchronous job is polled (#4145)");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_AsyncRawMultipleValues_ReturnsMultipartRelated()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{MultiRawJobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("multipart/related");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Content-ID: <outputFeatureLayer>");
        body.Should().Contain("Content-ID: <outputReport>");
        body.Should().Contain("{\"value\":42}");
        body.Should().Contain("{\"count\":1}");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_CanonicalPlanRunner_PreservesArtifactDocument()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{CanonicalJobId}/results");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("outputFeatureLayer").GetProperty("href").GetString()
            .Should().Be("https://example.test/ogc-buffer-output.geojson");
    }
}

/// <summary>
/// Per-class wrapper that owns a single <see cref="WebAppFixture"/> with a fixed
/// succeeded artifact-backed job store and geoprocessing job service registered
/// once, shared across all tests in <see cref="OgcProcessesJobResultsTests"/> via
/// <see cref="IClassFixture{T}"/>. Both tests only read the fixed job id, so the
/// shared fixture is safe.
/// </summary>
public sealed class OgcProcessesJobResultsTestsFixture : IAsyncLifetime
{
    private const string JobId = "ogc-gp-result-job";
    private const string SelectedJobId = "ogc-gp-selected-result-job";
    private const string ValueJobId = "ogc-gp-value-result-job";
    private const string RawJobId = "ogc-gp-raw-result-job";
    private const string MultiRawJobId = "ogc-gp-multi-raw-result-job";
    private const string CanonicalJobId = "ogc-gp-canonical-result-job";

    public WebAppFixture App { get; }

    public OgcProcessesJobResultsTestsFixture()
    {
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(CreateSucceededJob());
        jobStore.GetAsync(SelectedJobId, Arg.Any<CancellationToken>()).Returns(CreateSelectedJob());
        jobStore.GetAsync(ValueJobId, Arg.Any<CancellationToken>()).Returns(CreateValueJob(ValueJobId, "document"));
        jobStore.GetAsync(RawJobId, Arg.Any<CancellationToken>()).Returns(CreateValueJob(RawJobId, "raw"));
        jobStore.GetAsync(MultiRawJobId, Arg.Any<CancellationToken>()).Returns(CreateMultiRawJob());
        jobStore.GetAsync(CanonicalJobId, Arg.Any<CancellationToken>()).Returns(CreateValueJob(CanonicalJobId, "document", "honua-geoprocessing"));

        App = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton(jobStore);
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new ArtifactBackedJobService());
            });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private static ExecutionJobRecord CreateSucceededJob()
        => new()
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = ["https://example.test/ogc-buffer-output.geojson"],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geometry-buffer",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "geometry-buffer-plan",
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "geometry.buffer",
                    [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer",
                    [$"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}0"] = "outputFeatureLayer"
                }
            }
        };

    private static ExecutionJobRecord CreateSelectedJob()
        => new()
        {
            OperationId = SelectedJobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences =
            [
                "https://example.test/dissolve-features.geojson",
                "https://example.test/dissolve-summary.json"
            ],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "generalization-dissolve",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "generalization-dissolve-plan",
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "generalization.dissolve",
                    [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer|Table",
                    [$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}1"] = "outputTable",
                    ["submittedVia"] = "OGC-API-Processes",
                    ["protocolProcessId"] = "generalization.dissolve"
                }
            }
        };

    private static ExecutionJobRecord CreateValueJob(string jobId, string responseMode, string processId = "geometry.buffer")
        => new()
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geometry-buffer",
                Parameters = new Dictionary<string, string>
                {
                    ["submittedVia"] = "OGC-API-Processes",
                    ["protocolProcessId"] = processId,
                    ["ogc.processes.response"] = responseMode,
                    [$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}0"] = "outputFeatureLayer"
                }
            }
        };

    private static ExecutionJobRecord CreateMultiRawJob()
        => new()
        {
            OperationId = MultiRawJobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "multi-output",
                Parameters = new Dictionary<string, string>
                {
                    ["submittedVia"] = "OGC-API-Processes",
                    ["protocolProcessId"] = "example.multi-output",
                    ["ogc.processes.response"] = "raw",
                    [$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}0"] = "outputFeatureLayer",
                    [$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}1"] = "outputReport"
                }
            }
        };

    private sealed class ArtifactBackedJobService : IGeoprocessingJobService
    {

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoprocessingJobListPage { Items = Array.Empty<ExecutionJobRecord>() });
        private static readonly AnalysisResultPackage Results = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "ogc-gp-result-job:v1",
            summary: new ResultSummary
            {
                Title = "geometry.buffer results",
                Description = "Produced 1 artifact."
            },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "artifact-output-1",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "outputFeatureLayer",
                    Uri = "https://example.test/ogc-buffer-output.geojson",
                    ContentType = "application/geo+json",
                    Metadata = new Dictionary<string, string>
                    {
                        [GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey] = "outputFeatureLayer"
                    }
                },
                new ArtifactRef
                {
                    ArtifactId = "artifact-output-2",
                    Kind = ArtifactKind.Report,
                    Label = "outputFeatureLayer",
                    Uri = "https://example.test/ogc-buffer-output-summary.json",
                    ContentType = "application/json",
                    Metadata = new Dictionary<string, string>
                    {
                        [GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey] = "outputFeatureLayer"
                    }
                }
            ],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"],
                ExecutedAt = DateTimeOffset.UtcNow
            });

        private static readonly AnalysisResultPackage SelectedResults = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "ogc-gp-selected-result-job:v1",
            summary: new ResultSummary
            {
                Title = "generalization.dissolve results",
                Description = "Produced 2 artifacts."
            },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "selected-artifact-1",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "featureLayer1",
                    Uri = "https://example.test/dissolve-features.geojson",
                    ContentType = "application/geo+json"
                },
                new ArtifactRef
                {
                    ArtifactId = "selected-artifact-2",
                    Kind = ArtifactKind.Table,
                    Label = "outputTable",
                    Uri = "https://example.test/dissolve-summary.json",
                    ContentType = "application/json",
                    Metadata = new Dictionary<string, string>
                    {
                        [GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey] = "outputTable"
                    }
                }
            ],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["generalization.dissolve"],
                ExecutedAt = DateTimeOffset.UtcNow
            });

        private static readonly AnalysisResultPackage ValueResults = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "ogc-gp-value-result-job:v1",
            summary: new ResultSummary { Title = "inline GeoJSON" },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "inline-feature-collection",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "outputFeatureLayer",
                    Uri = "data:application/geo+json;base64," + Convert.ToBase64String(
                        Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\",\"features\":[]}")),
                    ContentType = "application/geo+json"
                }
            ],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"],
                ExecutedAt = DateTimeOffset.UtcNow
            });

        private static readonly AnalysisResultPackage RawResults = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "ogc-gp-raw-result-job:v1",
            summary: new ResultSummary { Title = "raw JSON" },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "raw-value",
                    Kind = ArtifactKind.Scalar,
                    Label = "outputFeatureLayer",
                    Uri = "data:application/json;base64,eyJ2YWx1ZSI6NDJ9",
                    ContentType = "application/json"
                }
            ],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"],
                ExecutedAt = DateTimeOffset.UtcNow
            });

        private static readonly AnalysisResultPackage MultiRawResults = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "ogc-gp-multi-raw-result-job:v1",
            summary: new ResultSummary { Title = "multiple raw values" },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "multi-value-1",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "outputFeatureLayer",
                    Uri = "data:application/json;base64,eyJ2YWx1ZSI6NDJ9",
                    ContentType = "application/json"
                },
                new ArtifactRef
                {
                    ArtifactId = "multi-value-2",
                    Kind = ArtifactKind.Report,
                    Label = "outputReport",
                    Uri = "data:application/json;base64,eyJjb3VudCI6MX0=",
                    ContentType = "application/json"
                }
            ],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["example.multi-output"],
                ExecutedAt = DateTimeOffset.UtcNow
            });

        public Task EnsureCallerAuthorizedAsync(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
            AnalysisPlan plan,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(plan);

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
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(jobId switch
            {
                SelectedJobId => CreateSelectedJob(),
                ValueJobId => CreateValueJob(ValueJobId, "document"),
                RawJobId => CreateValueJob(RawJobId, "raw"),
                MultiRawJobId => CreateMultiRawJob(),
                CanonicalJobId => CreateValueJob(CanonicalJobId, "document", "honua-geoprocessing"),
                _ => CreateSucceededJob()
            });

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(jobId switch
            {
                SelectedJobId => SelectedResults,
                ValueJobId => ValueResults,
                RawJobId => RawResults,
                MultiRawJobId => MultiRawResults,
                _ => Results
            });

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// #2753 IDOR regression: GET /ogc/processes/jobs/{jobId} must enforce per-job
/// ownership through the shared service path. A job that exists in the store (so it
/// clears the coarse Job.Read gate and the geoprocessing-kind check) but whose
/// ownership check denies the caller must surface as a 404 — not a 200 leaking the
/// other owner's job status.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesJobStatusOwnershipTests : IClassFixture<OgcProcessesJobStatusOwnershipTestsFixture>
{
    private const string JobId = "ogc-gp-foreign-job";

    private readonly WebAppFixture _fixture;

    public OgcProcessesJobStatusOwnershipTests(OgcProcessesJobStatusOwnershipTestsFixture fixture)
        => _fixture = fixture.App;

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_NonOwnedJob_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the ownership-enforcing service path denies a non-owned job, and the endpoint " +
            "must map that denial to a not-found rather than leaking the job status (#2753)");
    }
}

/// <summary>
/// Fixture for <see cref="OgcProcessesJobStatusOwnershipTests"/>: the store returns a
/// geoprocessing job so the endpoint reaches the ownership check, but the job service's
/// ownership-enforcing GetJobAsync denies it (as the real service does for a non-owner).
/// </summary>
public sealed class OgcProcessesJobStatusOwnershipTestsFixture : IAsyncLifetime
{
    private const string JobId = "ogc-gp-foreign-job";

    public WebAppFixture App { get; }

    public OgcProcessesJobStatusOwnershipTestsFixture()
    {
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(CreateForeignJob());

        App = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton(jobStore);
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new OwnershipDenyingJobService());
            });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private static ExecutionJobRecord CreateForeignJob()
        => new()
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo { RequestedBy = "some-other-owner" },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geometry-buffer"
            }
        };

    /// <summary>
    /// An <see cref="IGeoprocessingJobService"/> whose ownership-enforcing GetJobAsync
    /// denies every caller (surfaced as not-found, exactly as the real service does for a
    /// non-owner), so the endpoint's deny→404 mapping is exercised end to end.
    /// </summary>
    private sealed class OwnershipDenyingJobService : IGeoprocessingJobService
    {
        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoprocessingJobListPage { Items = Array.Empty<ExecutionJobRecord>() });

        public Task EnsureCallerAuthorizedAsync(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
            AnalysisPlan plan,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(plan);

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
            => throw new NotSupportedException();

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
}

/// <summary>
/// Integration coverage for the OGC API Processes GET /jobs/{jobId}/results failed-job case.
/// PA-205: results for a failed job must use a registered OGC exception type URI, not about:blank.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesFailedJobResultsTests : IClassFixture<OgcProcessesFailedJobResultsTestsFixture>
{
    private const string JobId = "ogc-gp-failed-job";

    private readonly WebAppFixture _fixture;

    public OgcProcessesFailedJobResultsTests(OgcProcessesFailedJobResultsTestsFixture fixture)
        => _fixture = fixture.App;

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_FailedJob_Returns500WithRegisteredOgcExceptionType()
    {
        // OGC API Processes Part 1 (OGC 18-062r2): a registered OGC exception type URI
        // must be used so clients and CITE test runners can distinguish job failures from
        // generic server errors. "about:blank" is not acceptable here.
        using var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{JobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("type").GetString().Should()
            .Be("http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/job-failed",
                "failed job results must use a registered OGC exception type URI, not about:blank");
    }
}

/// <summary>
/// Fixture that seeds a single failed job for <see cref="OgcProcessesFailedJobResultsTests"/>.
/// </summary>
public sealed class OgcProcessesFailedJobResultsTestsFixture : IAsyncLifetime
{
    private const string JobId = "ogc-gp-failed-job";

    public WebAppFixture App { get; }

    public OgcProcessesFailedJobResultsTestsFixture()
    {
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(CreateFailedJob());

        App = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton(jobStore);
            });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private static ExecutionJobRecord CreateFailedJob()
        => new()
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Process execution failed due to invalid input geometry.",
            Audit = new OperationAuditInfo { RequestedBy = "admin" },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geometry-buffer",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "geometry-buffer-plan",
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "geometry.buffer",
                    [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer"
                }
            }
        };
}
