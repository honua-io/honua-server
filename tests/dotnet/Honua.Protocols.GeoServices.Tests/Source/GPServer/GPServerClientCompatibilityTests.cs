// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Client-shaped integration tests for the GPServer adapter. These tests keep
/// route binding real while exercising the endpoint sequence a typed SDK would use.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerClientCompatibilityTests : IAsyncLifetime
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TypedClient_ReadsServiceAndTaskMetadata()
    {
        var client = new GPServerClient(_fixture.Client, ServiceId);

        var service = await client.GetServiceInfoAsync();
        var task = await client.GetTaskInfoAsync("geometry.buffer");

        service.CurrentVersion.Should().Be(10.81);
        service.ExecutionType.Should().Be("esriExecutionTypeAsynchronous");
        service.Tasks.Should().Contain("geometry.buffer");

        task.Name.Should().Be("geometry.buffer");
        // ADVERTISED executionType stays asynchronous (the established contract that
        // cross-repo integration tests assert). The synchronous execute route is
        // purely additive and self-gates via GPServerExecutionPolicy; it does not
        // change advertised task metadata. See #1228.
        task.ExecutionType.Should().Be("esriExecutionTypeAsynchronous");
        task.Parameters.Should().Contain(parameter => parameter.Name == "wkb" && parameter.Direction == "esriGPParameterDirectionInput");
        task.Parameters.Should().Contain(parameter => parameter.Name == "distance" && parameter.DataType == "GPDouble");
        task.Parameters.Should().Contain(parameter => parameter.Name == "outputFeatureLayer" && parameter.Direction == "esriGPParameterDirectionOutput");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task TypedClient_SubmitsPollsAndReadsNamedResult()
    {
        var jobService = new CompletedJobService(ServiceId, "geometry.buffer");
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(jobService);
            });

        await fixture.InitializeAsync();
        try
        {
            var client = new GPServerClient(fixture.CreateAdminClient(), ServiceId);

            var submit = await client.SubmitBufferJobAsync(PointWkbBase64, 4326, 25.5);
            var status = await client.GetJobStatusAsync("geometry.buffer", submit.JobId);
            var result = await client.GetJobResultAsync("geometry.buffer", submit.JobId, "outputFeatureLayer");

            submit.JobId.Should().Be("gp-client-job");
            submit.JobStatus.Should().Be("esriJobSubmitted");
            status.JobId.Should().Be("gp-client-job");
            status.JobStatus.Should().Be("esriJobSucceeded");
            status.Results.Should().ContainKey("outputFeatureLayer");
            result.ParamName.Should().Be("outputFeatureLayer");
            result.DataType.Should().Be("GPFeatureRecordSetLayer");
            result.Value.Should().Be("https://example.test/gp-client-output.geojson");

            jobService.LastSubmittedPlan.Should().NotBeNull();
            jobService.LastSubmittedPlan!.Steps.Should().ContainSingle(step => step.ProcessId == "geometry.buffer");
            jobService.LastSubmittedProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("submittedVia", "GPServer"));
            jobService.LastSubmittedProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("gpserver.serviceId", ServiceId));
            jobService.LastSubmittedProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("gpserver.taskName", "geometry.buffer"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task TypedClient_UnsupportedEnvironmentControlsReturnEsriError()
    {
        var client = new GPServerClient(_fixture.Client, ServiceId);

        // The async submitJob contract rejects ALL env:* controls with a clear Esri
        // 400. env:outSR / env:processSR are honored only on the additive synchronous
        // execute route (see the dedicated execute outSR tests), not on submitJob.
        var error = await client.SubmitBufferJobExpectingErrorAsync(
            PointWkbBase64,
            4326,
            25.5,
            new KeyValuePair<string, string>("env:transferDomains", "true"));

        error.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        error.Code.Should().Be(400);
        error.Message.Should().Be("Bad Request");
        error.Details.Should().Contain(detail => detail.Contains("GP environment controls are not yet supported", StringComparison.Ordinal));
        error.Details.Should().Contain(detail => detail.Contains("env:transferDomains", StringComparison.Ordinal));
    }

    private sealed class GPServerClient(HttpClient httpClient, string serviceId)
    {
        public async Task<GPServiceInfo> GetServiceInfoAsync()
        {
            using var response = await httpClient.GetAsync($"/rest/services/{serviceId}/GPServer?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            var root = doc.RootElement;
            return new GPServiceInfo(
                root.GetProperty("currentVersion").GetDouble(),
                root.GetProperty("executionType").GetString()!,
                root.GetProperty("tasks").EnumerateArray().Select(task => task.GetString()!).ToArray());
        }

        public async Task<GPTaskInfo> GetTaskInfoAsync(string taskName)
        {
            using var response = await httpClient.GetAsync($"/rest/services/{serviceId}/GPServer/{taskName}?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            var root = doc.RootElement;
            return new GPTaskInfo(
                root.GetProperty("name").GetString()!,
                root.GetProperty("executionType").GetString()!,
                root.GetProperty("parameters").EnumerateArray()
                    .Select(parameter => new GPParameter(
                        parameter.GetProperty("name").GetString()!,
                        parameter.GetProperty("direction").GetString()!,
                        parameter.GetProperty("dataType").GetString()!))
                    .ToArray());
        }

        public async Task<GPSubmitResult> SubmitBufferJobAsync(string wkb, int srid, double distance)
        {
            using var content = BuildSubmitContent(wkb, srid, distance);
            using var response = await httpClient.PostAsync($"/rest/services/{serviceId}/GPServer/geometry.buffer/submitJob", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            var root = doc.RootElement;
            return new GPSubmitResult(
                root.GetProperty("jobId").GetString()!,
                root.GetProperty("jobStatus").GetString()!);
        }

        public async Task<GPJobStatus> GetJobStatusAsync(string taskName, string jobId)
        {
            using var response = await httpClient.GetAsync($"/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            var root = doc.RootElement;
            return new GPJobStatus(
                root.GetProperty("jobId").GetString()!,
                root.GetProperty("jobStatus").GetString()!,
                root.GetProperty("results").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetProperty("paramUrl").GetString()!));
        }

        public async Task<GPResult> GetJobResultAsync(string taskName, string jobId, string paramName)
        {
            using var response = await httpClient.GetAsync($"/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            var root = doc.RootElement;
            return new GPResult(
                root.GetProperty("paramName").GetString()!,
                root.GetProperty("dataType").GetString()!,
                root.GetProperty("value").GetString()!);
        }

        public async Task<GPError> SubmitBufferJobExpectingErrorAsync(
            string wkb,
            int srid,
            double distance,
            KeyValuePair<string, string> extraParameter)
        {
            using var content = BuildSubmitContent(wkb, srid, distance, extraParameter);
            using var response = await httpClient.PostAsync($"/rest/services/{serviceId}/GPServer/geometry.buffer/submitJob", content);
            using var doc = await ReadJsonAsync(response);
            var error = doc.RootElement.GetProperty("error");
            return new GPError(
                response.StatusCode,
                error.GetProperty("code").GetInt32(),
                error.GetProperty("message").GetString()!,
                error.GetProperty("details").EnumerateArray().Select(detail => detail.GetString()!).ToArray());
        }

        private static FormUrlEncodedContent BuildSubmitContent(
            string wkb,
            int srid,
            double distance,
            KeyValuePair<string, string>? extraParameter = null)
        {
            var values = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = wkb,
                ["srid"] = srid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["distance"] = distance.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            if (extraParameter is { } extra)
            {
                values[extra.Key] = extra.Value;
            }

            return new FormUrlEncodedContent(values);
        }

        private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private sealed record GPServiceInfo(double CurrentVersion, string ExecutionType, string[] Tasks);

    private sealed record GPTaskInfo(string Name, string ExecutionType, GPParameter[] Parameters);

    private sealed record GPParameter(string Name, string Direction, string DataType);

    private sealed record GPSubmitResult(string JobId, string JobStatus);

    private sealed record GPJobStatus(string JobId, string JobStatus, Dictionary<string, string> Results);

    private sealed record GPResult(string ParamName, string DataType, string Value);

    private sealed record GPError(HttpStatusCode StatusCode, int Code, string Message, string[] Details);

    private sealed class CompletedJobService(string serviceId, string taskName) : IGeoprocessingJobService
    {
        private readonly ExecutionJobRecord _completedJob = new()
        {
            OperationId = "gp-client-job",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "gp-client",
                Parameters = new Dictionary<string, string>
                {
                    ["gpserver.serviceId"] = serviceId,
                    ["gpserver.taskName"] = taskName,
                    ["gpserver.output.0"] = "outputFeatureLayer",
                },
            },
        };

        private readonly AnalysisResultPackage _results = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "pkg-gp-client-job",
            summary: new ResultSummary { Title = "GPServer client output" },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "art-gp-client-output",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "Buffered Output",
                    Uri = "https://example.test/gp-client-output.geojson",
                },
            ],
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"],
                ExecutedAt = DateTimeOffset.UtcNow,
            });

        public AnalysisPlan? LastSubmittedPlan { get; private set; }

        public IReadOnlyDictionary<string, string> LastSubmittedProtocolMetadata { get; private set; } =
            new Dictionary<string, string>();

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
            LastSubmittedPlan = plan;
            LastSubmittedProtocolMetadata = protocolMetadata ?? new Dictionary<string, string>();

            return Task.FromResult(_completedJob with
            {
                Status = ExecutionJobStatus.Queued,
                Spec = _completedJob.Spec with
                {
                    Parameters = new Dictionary<string, string>(LastSubmittedProtocolMetadata),
                },
            });
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_completedJob);

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_results);

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
