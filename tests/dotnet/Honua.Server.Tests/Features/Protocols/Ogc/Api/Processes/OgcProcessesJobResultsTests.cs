// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Helpers;
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
[Collection("Database")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesJobResultsTests : IAsyncLifetime
{
    private const string JobId = "ogc-gp-result-job";

    private readonly WebAppFixture _fixture;

    public OgcProcessesJobResultsTests()
    {
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(CreateSucceededJob());

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton(jobStore);
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new ArtifactBackedJobService());
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

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

    private sealed class ArtifactBackedJobService : IGeoprocessingJobService
    {
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

        public void EnsureCallerAuthorized(ClaimsPrincipal principal, OperatorResourceType resourceType, OperatorOperation operation)
        {
        }

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
            => Task.FromResult(CreateSucceededJob());

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Results);

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
