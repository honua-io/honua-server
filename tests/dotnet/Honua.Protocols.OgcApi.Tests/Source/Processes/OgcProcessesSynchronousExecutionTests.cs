// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesSynchronousExecutionTests : IClassFixture<OgcProcessesSynchronousExecutionFixture>
{
    private readonly OgcProcessesSynchronousExecutionFixture _fixture;

    public OgcProcessesSynchronousExecutionTests(OgcProcessesSynchronousExecutionFixture fixture)
        => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task GeometryBuffer_GeoJsonRaw_DefaultsToSynchronousExecution()
    {
        const string geoJson = """{"type":"Feature","geometry":{"type":"Point","coordinates":[0,0]},"properties":{"buffered":true}}""";
        var encodedResult = Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson));
        _fixture.JobService.ResultUri = $"data:application/geo+json;base64,{encodedResult}";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution")
        {
            Content = new StringContent(
                """{"inputs":{"wkb":{"type":"Point","coordinates":[0,0]},"srid":4326,"distance":25},"response":"raw"}""",
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
        (await response.Content.ReadAsStringAsync()).Should().Be(geoJson);
        response.Headers.GetValues("Preference-Applied").Should().Contain("respond-sync");
        _fixture.JobService.SubmittedPlan.Should().NotBeNull();
        _fixture.JobService.SubmittedPlan!.Steps.Single().ProcessId.Should().Be("geometry.buffer");
        Convert.FromBase64String(_fixture.JobService.SubmittedPlan.Steps.Single().Inputs["wkb"])
            .Should().NotBeEmpty("GeoJSON is normalized to the catalog's canonical WKB input");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task GeometryBuffer_WrappedGeoJson_RespondAsync_IsAccepted()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"wkb":{"value":{"type":"Point","coordinates":[1,2]},"mediaType":"application/geo+json"},"srid":4326,"distance":10}}""",
            Encoding.UTF8,
            "application/json");

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("Preference-Applied").Should().Contain("respond-async");
        Convert.FromBase64String(_fixture.JobService.SubmittedPlan!.Steps.Single().Inputs["wkb"])
            .Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task GeometryBuffer_InvalidGeoJson_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution")
        {
            Content = new StringContent(
                """{"inputs":{"wkb":{"type":"Point","coordinates":"invalid"},"srid":4326,"distance":10}}""",
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class OgcProcessesSynchronousExecutionFixture : IAsyncLifetime
{
    internal CapturingJobService JobService { get; } = new();

    public WebAppFixture App { get; }

    public OgcProcessesSynchronousExecutionFixture()
    {
        App = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton<IGeoprocessingJobService>(JobService);
        });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    internal sealed class CapturingJobService : IGeoprocessingJobService
    {
        private const string JobId = "ogc-sync-geometry-job";

        public string ResultUri { get; set; } = "data:application/geo+json;base64,e30=";

        public AnalysisPlan? SubmittedPlan { get; private set; }

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
        {
            SubmittedPlan = plan;
            return Task.FromResult(CreateSucceededJob());
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSucceededJob());

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoprocessingJobListPage { Items = [] });

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AnalysisResultPackage.CreateCompleted(
                resultPackageId: $"{JobId}:result",
                summary: new ResultSummary { Title = "Buffered geometry" },
                artifacts:
                [
                    new ArtifactRef
                    {
                        ArtifactId = "buffer-output",
                        Kind = ArtifactKind.FeatureLayer,
                        Label = "outputFeatureLayer",
                        Uri = ResultUri,
                        ContentType = "application/geo+json"
                    }
                ],
                workspaceRefs: [],
                provenance: new ProvenanceRecord
                {
                    Sources = [],
                    ProcessDefinitions = ["geometry.buffer"],
                    ExecutedAt = DateTimeOffset.UtcNow
                }));

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static ExecutionJobRecord CreateSucceededJob()
            => new()
            {
                OperationId = JobId,
                Status = ExecutionJobStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "geometry.buffer",
                    Parameters = new Dictionary<string, string>()
                }
            };
    }
}
