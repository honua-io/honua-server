// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Integration tests for GPServer REST endpoints operating as a protocol adapter
/// over the canonical process runtime.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerEndpointTests : IAsyncLifetime
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
    // Service Info
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_ReturnsGPServerMetadata()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("currentVersion").GetDouble().Should().Be(10.81);
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
        root.TryGetProperty("capabilities", out _).Should().BeTrue();
        root.GetProperty("resultMapServerName").GetString().Should().BeEmpty();
        root.GetProperty("tasks").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("tasks").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain("geometry.buffer");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_Post_ReturnsGPServerMetadata()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json"
        });

        var response = await _client.PostAsync($"/rest/services/{ServiceId}/GPServer", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain("geometry.buffer");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_PostWithUnsupportedFormat_ReturnsBadRequest()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "html"
        });

        var response = await _client.PostAsync($"/rest/services/{ServiceId}/GPServer", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_PostWithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer",
            new StringContent("""{"f":"json"}""", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer?f=html");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_UnknownService_ReturnsNotFound()
    {
        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator.ValidateServiceV2Async("MissingService", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.NotFound<MetadataV2Service>("Service 'MissingService' was not found."));

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IResourceValidator>();
                services.AddSingleton(resourceValidator);
            });

        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.Client.GetAsync("/rest/services/MissingService/GPServer");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Task Info
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_KnownTask_ReturnsTaskMetadata()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer/geometry.buffer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be("geometry.buffer");
        root.GetProperty("displayName").GetString().Should().Be("Buffer");
        root.GetProperty("description").GetString().Should().Contain("Creates a polygon");
        root.GetProperty("category").GetString().Should().Be("geometry");
        root.GetProperty("helpUrl").GetString().Should().BeEmpty();
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");

        var parameters = root.GetProperty("parameters").EnumerateArray().ToArray();
        parameters.Should().Contain(parameter =>
            parameter.GetProperty("name").GetString() == "distance" &&
            parameter.GetProperty("description").GetString() == "Buffer distance in meters." &&
            parameter.GetProperty("direction").GetString() == "esriGPParameterDirectionInput" &&
            parameter.GetProperty("dataType").GetString() == "GPDouble");
        parameters.Should().Contain(parameter =>
            parameter.GetProperty("name").GetString() == "wkb" &&
            parameter.GetProperty("description").GetString()!.Contains("base64-encoded WKB", StringComparison.Ordinal) &&
            parameter.GetProperty("direction").GetString() == "esriGPParameterDirectionInput");
        parameters.Should().Contain(parameter =>
            parameter.GetProperty("name").GetString() == "outputFeatureLayer" &&
            parameter.GetProperty("direction").GetString() == "esriGPParameterDirectionOutput");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_Post_KnownTask_ReturnsTaskMetadata()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json"
        });

        var response = await _client.PostAsync($"/rest/services/{ServiceId}/GPServer/geometry.buffer", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("geometry.buffer");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_PostWithUnsupportedFormat_ReturnsBadRequest()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "html"
        });

        var response = await _client.PostAsync($"/rest/services/{ServiceId}/GPServer/geometry.buffer", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_UnknownTask_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/rest/services/{ServiceId}/GPServer/BufferAnalysis");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_ProtocolDisabledService_ReturnsNotFound()
    {
        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator.ValidateServiceV2Async(ServiceId, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(CreateGpServerServiceV2(gpServerEnabled: false)));

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IResourceValidator>();
                services.AddSingleton(resourceValidator);
            });

        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.Client.GetAsync($"/rest/services/{ServiceId}/GPServer/geometry.buffer");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Execute (synchronous)
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecutePost_SyncEligibleTask_ReturnsInlineResults()
    {
        var executeFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new SyncExecuteGeoprocessingJobService());
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
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/execute", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded");
            var results = root.GetProperty("results").EnumerateArray().ToArray();
            results.Should().NotBeEmpty();
            results[0].GetProperty("paramName").GetString().Should().Be("outputFeatureLayer");
            results[0].GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
        }
        finally
        {
            await executeFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecuteGet_SyncEligibleTask_ReturnsInlineResults()
    {
        var executeFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new SyncExecuteGeoprocessingJobService());
            });

        await executeFixture.InitializeAsync();
        try
        {
            using var client = executeFixture.CreateAdminClient();

            var response = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/execute?f=json&wkb={Uri.EscapeDataString(PointWkbBase64)}&srid=4326&distance=10");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded");
        }
        finally
        {
            await executeFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecutePost_AsyncOnlyTask_Returns400WithCapabilityMessage()
    {
        // analytics.cluster is NOT in GPServerExecutionPolicy.SyncEligibleProcessIds —
        // the synchronous /execute path must surface a capability error rather
        // than try to run a long-running task inline.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["layerId"] = "1",
            ["algorithm"] = "dbscan",
            ["eps"] = "10",
            ["minPoints"] = "3"
        });

        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/analytics.cluster/execute", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("submitJob");
        body.Should().Contain("asynchronous");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecutePost_WithEnvOutSR_HonorsControl()
    {
        var executeFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new SyncExecuteGeoprocessingJobService());
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
                ["distance"] = "10",
                ["env:outSR"] = "3857"
            });

            var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/execute", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "env:outSR is accepted on the synchronous execute route (unlike submitJob which rejects all env controls)");
        }
        finally
        {
            await executeFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecutePost_InvalidGPChoice_Returns400()
    {
        // conversion.geometry-format declares AllowedValues=[wkt,geojson,wkb,ewkt]
        // on its 'target' parameter; an out-of-set value must be rejected before
        // the canonical pipeline is touched.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["geometry"] = PointWkbBase64,
            ["target"] = "not-a-real-format"
        });

        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/conversion.geometry-format/execute", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("target");
        body.Should().Contain("not-a-real-format");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_ParameterWithAllowedValues_PopulatesChoiceList()
    {
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/conversion.geometry-format");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var target = doc.RootElement.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "target");
        target.TryGetProperty("choiceList", out var choices).Should().BeTrue(
            "target carries an enum constraint and must surface it as choiceList");
        choices.EnumerateArray().Select(c => c.GetString()).Should()
            .BeEquivalentTo(["wkt", "geojson", "wkb", "ewkt"]);
    }

    // -----------------------------------------------------------------------
    // SubmitJob
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnknownTask_ReturnsNotFound()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["input_features"] = "test-layer",
            ["buffer_distance"] = "100"
        });

        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/submitJob", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob",
            new StringContent("""{"f":"json"}""", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJobGet_UnknownTask_ReturnsNotFound()
    {
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/submitJob?f=json&input=test");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ProtocolDisabledService_ReturnsNotFound()
    {
        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator.ValidateServiceV2Async(ServiceId, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(CreateGpServerServiceV2(gpServerEnabled: false)));

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IResourceValidator>();
                services.AddSingleton(resourceValidator);
            });

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "25.5"
            });

            using var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CatalogStorageUnavailable_ReturnsServiceUnavailable()
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IResourceValidator>();
                services.AddSingleton<IResourceValidator>(
                    new ThrowingResourceValidator(new PostgresException(
                        "relation \"honua.services\" does not exist",
                        "ERROR",
                        "ERROR",
                        PostgresErrorCodes.UndefinedTable)));
            });

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "25.5"
            });

            using var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("honua.services");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_KnownTask_BuildsPlanAndStoresProtocolMetadata()
    {
        var recordingService = new RecordingGeoprocessingJobService();
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
                ["distance"] = "25.5",
                ["geodesic"] = "true",
                ["context"] = "{\"extent\":{}}"
            });

            var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            root.GetProperty("jobId").GetString().Should().Be("gp-job-123");
            root.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");

            recordingService.LastPlan.Should().NotBeNull();
            recordingService.LastPlan!.Steps.Should().ContainSingle();
            var step = recordingService.LastPlan.Steps[0];
            step.Kind.Should().Be(AnalysisPlanStepKind.Geoprocess);
            step.ProcessId.Should().Be("geometry.buffer");
            step.Inputs.Should().Contain(new KeyValuePair<string, string>("wkb", PointWkbBase64));
            step.Inputs.Should().Contain(new KeyValuePair<string, string>("srid", "4326"));
            step.Inputs.Should().Contain(new KeyValuePair<string, string>("distance", "25.5"));
            step.Inputs.Should().Contain(new KeyValuePair<string, string>("geodesic", "true"));
            step.Inputs.Should().NotContainKey("f");
            step.Inputs.Should().NotContainKey("context");

            recordingService.LastProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("submittedVia", "GPServer"));
            recordingService.LastProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("gpserver.serviceId", ServiceId));
            recordingService.LastProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("gpserver.taskName", "geometry.buffer"));
            recordingService.LastProtocolMetadata.Should().Contain(new KeyValuePair<string, string>("gpserver.context", "{\"extent\":{}}"));
        }
        finally
        {
            await submitFixture.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Job Status
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithInvalidJobId_Returns404()
    {
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/nonexistent-job-id?f=json");

        // Without Redis: ServiceUnavailable; With Redis: NotFound
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Job Result
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task JobResult_WithInvalidJobId_ReturnsError()
    {
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/nonexistent/results/Output?f=json");

        // Without Redis: ServiceUnavailable; With Redis: NotFound
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_SucceededJobWithResultPackage_ReturnsNamedResultReferences()
    {
        var resultFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new ResultBackedGeoprocessingJobService());
            });

        await resultFixture.InitializeAsync();
        try
        {
            using var client = resultFixture.CreateAdminClient();

            var response = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/jobs/gp-result-job?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var outputFeatureLayer = doc.RootElement.GetProperty("results").GetProperty("outputFeatureLayer");
            outputFeatureLayer.GetProperty("paramUrl").GetString().Should().Be("results/outputFeatureLayer");
        }
        finally
        {
            await resultFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task JobResult_WithResultPackage_ReturnsNamedOutput()
    {
        var resultFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(new ResultBackedGeoprocessingJobService());
            });

        await resultFixture.InitializeAsync();
        try
        {
            using var client = resultFixture.CreateAdminClient();

            var response = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/jobs/gp-result-job/results/outputFeatureLayer?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("paramName").GetString().Should().Be("outputFeatureLayer");
            root.GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
            root.GetProperty("value").GetString().Should().Be("https://example.test/artifacts/output.geojson");
        }
        finally
        {
            await resultFixture.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Cancel Job
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithInvalidJobId_ReturnsError()
    {
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/nonexistent/cancel?f=json");

        // Without Redis: ServiceUnavailable; With Redis: NotFound
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJobPost_WithInvalidJobId_ReturnsError()
    {
        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/nonexistent/cancel",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }));

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Route binding validation
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithMismatchedService_ReturnsNotFound()
    {
        // Create a job directly via the store with GPServer binding metadata.
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            // Without Redis, skip binding validation — store unavailable
            return;
        }

        var jobId = $"gpbind-svc-{Guid.NewGuid():N}";
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "gptest",
                Parameters = new Dictionary<string, string>
                {
                    ["gpserver.serviceId"] = ServiceId,
                    ["gpserver.taskName"] = "BufferAnalysis"
                }
            }
        };

        var created = await jobStore.TryCreateAsync(jobRecord);
        if (!created)
        {
            return;
        }

        // Query status under a different service — should be rejected
        var statusResponse = await _client.GetAsync(
            $"/rest/services/OtherService/GPServer/BufferAnalysis/jobs/{jobId}?f=json");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithMismatchedTask_ReturnsNotFound()
    {
        // Create a job directly via the store with GPServer binding metadata.
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            // Without Redis, skip binding validation — store unavailable
            return;
        }

        var jobId = $"gpbind-task-{Guid.NewGuid():N}";
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "gptest",
                Parameters = new Dictionary<string, string>
                {
                    ["gpserver.serviceId"] = ServiceId,
                    ["gpserver.taskName"] = "BufferAnalysis"
                }
            }
        };

        var created = await jobStore.TryCreateAsync(jobRecord);
        if (!created)
        {
            return;
        }

        // Query status under a different task — should be rejected
        var statusResponse = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/DifferentTask/jobs/{jobId}?f=json");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // Cross-protocol binding rejection
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithNonGPServerJob_ReturnsNotFound()
    {
        // Write a job record directly to the store without GPServer binding metadata.
        // This simulates a gRPC-submitted job that should not be visible via GPServer routes.
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            // Without Redis, skip — store unavailable
            return;
        }

        var jobId = $"grpc-test-{Guid.NewGuid():N}";
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "grpc-test"
                // No gpserver.serviceId / gpserver.taskName parameters
            }
        };

        var created = await jobStore.TryCreateAsync(jobRecord);
        if (!created)
        {
            return;
        }

        // Access via GPServer route — should be rejected (no GPServer binding metadata)
        var response = await _client.GetAsync(
            $"/rest/services/AnyService/GPServer/AnyTask/jobs/{jobId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // GP environment controls
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithEnvOutSR_ReturnsBadRequest()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
            ["env:outSR"] = "4326"
        });

        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJobPost_WithEnvInQueryString_ReturnsBadRequest()
    {
        // POST with form body but env control in query string — the query-string
        // parameter must still be read and rejected (not silently dropped).
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10"
        });

        var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob?env:outSR=4326", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJobGet_WithEnvProcessSR_ReturnsBadRequest()
    {
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob?f=json&wkb={Uri.EscapeDataString(PointWkbBase64)}&srid=4326&distance=10&env:processSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------------
    // Missing parameters
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithMissingJobId_ReturnsBadRequestOrNotFound()
    {
        // Empty jobId in the route — the routing framework will either 404 or match
        var response = await _client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/?f=json");

        response.IsSuccessStatusCode.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithRequestTimeout_Returns408()
    {
        var timeoutFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService, TimeoutGeoprocessingJobService>();
            });

        await timeoutFixture.InitializeAsync();
        try
        {
            using var client = timeoutFixture.CreateClient(c => c.Timeout = TimeSpan.FromSeconds(5));

            var response = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/slow-job?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        }
        finally
        {
            await timeoutFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithPreconditionFailure_Returns412()
    {
        var preconditionFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService, PreconditionFailedGeoprocessingJobService>();
            });

        await preconditionFixture.InitializeAsync();
        try
        {
            using var client = preconditionFixture.CreateClient(c => c.Timeout = TimeSpan.FromSeconds(5));

            var response = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/BufferAnalysis/jobs/completed-job?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }
        finally
        {
            await preconditionFixture.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Auth-before-validation ordering
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnauthenticatedWithInvalidEnvControls_Returns401BeforeBadRequest()
    {
        // Dedicated fixture with auth denial to verify auth-before-validation ordering
        // (see IGeoprocessingJobService contract: adapters must pre-authorize).
        var authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.RequiresAuth()));

        var authFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperatorAuthorizationEvaluator>();
                services.AddSingleton(authEvaluator);
            });

        await authFixture.InitializeAsync();
        try
        {
            var client = authFixture.Client;

            // Include env:outSR which would trigger a 400 if auth were skipped.
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "10",
                ["env:outSR"] = "4326"
            });

            var response = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob", content);

            // Auth must be checked before parameter validation — expect 401 not 400.
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "auth must be checked before env-control rejection per IGeoprocessingJobService contract");
        }
        finally
        {
            await authFixture.DisposeAsync();
        }
    }

    private sealed class TimeoutGeoprocessingJobService : IGeoprocessingJobService
    {
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

        public async Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new TimeoutException("Timed out waiting for GPServer job status.");
        }

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

    private sealed class PreconditionFailedGeoprocessingJobService : IGeoprocessingJobService
    {
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
            => throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' has not reached a terminal state.");

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

    private sealed class RecordingGeoprocessingJobService : IGeoprocessingJobService
    {
        public AnalysisPlan? LastPlan { get; private set; }

        public IReadOnlyDictionary<string, string>? LastProtocolMetadata { get; private set; }

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
        {
            LastPlan = plan;
            LastProtocolMetadata = protocolMetadata;

            return Task.FromResult(new ExecutionJobRecord
            {
                OperationId = "gp-job-123",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "gp-recording",
                    Parameters = protocolMetadata != null
                        ? new Dictionary<string, string>(protocolMetadata)
                        : new Dictionary<string, string>()
                }
            });
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
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

    private sealed class ResultBackedGeoprocessingJobService : IGeoprocessingJobService
    {
        private readonly ExecutionJobRecord _job = new()
        {
            OperationId = "gp-result-job",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "gp-results",
                Parameters = new Dictionary<string, string>
                {
                    ["gpserver.serviceId"] = ServiceId,
                    ["gpserver.taskName"] = "geometry.buffer",
                    ["gpserver.output.0"] = "outputFeatureLayer"
                }
            }
        };

        private readonly AnalysisResultPackage _results = AnalysisResultPackage.CreateCompleted(
            resultPackageId: "pkg-gp-result-job",
            summary: new ResultSummary { Title = "GPServer output" },
            artifacts:
            [
                new ArtifactRef
                {
                    ArtifactId = "art-output-1",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "Buffered Output",
                    Uri = "https://example.test/artifacts/output.geojson"
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
            => Task.FromResult(_job);

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_results);

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Test double for the synchronous /execute path: submission returns a
    /// terminal Succeeded job immediately, so the PollUntilTerminalAsync loop
    /// completes on the first tick. Submission echoes the protocol metadata
    /// back through the job's spec so the binding-validation gate (which
    /// reads gpserver.serviceId/gpserver.taskName from Parameters) sees the
    /// values that the adapter stamped on the plan.
    /// </summary>
    private sealed class SyncExecuteGeoprocessingJobService : IGeoprocessingJobService
    {
        private static readonly AnalysisResultPackage SuccessPackage =
            AnalysisResultPackage.CreateCompleted(
                resultPackageId: "pkg-gpserver-sync-execute",
                summary: new ResultSummary { Title = "Synchronous GP execute" },
                artifacts:
                [
                    new ArtifactRef
                    {
                        ArtifactId = "art-sync-execute-1",
                        Kind = ArtifactKind.FeatureLayer,
                        Label = "Buffered Output",
                        Uri = "https://example.test/artifacts/sync-execute.geojson"
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
        {
            var parameters = protocolMetadata != null
                ? new Dictionary<string, string>(protocolMetadata)
                : new Dictionary<string, string>();
            var jobId = $"gp-sync-{Guid.NewGuid():N}";
            return Task.FromResult(new ExecutionJobRecord
            {
                OperationId = jobId,
                Status = ExecutionJobStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "gp-sync-execute",
                    Parameters = parameters
                }
            });
        }

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobRecord
            {
                OperationId = jobId,
                Status = ExecutionJobStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "gp-sync-execute",
                    Parameters = new Dictionary<string, string>()
                }
            });

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

    private static MetadataV2Service CreateGpServerServiceV2(bool gpServerEnabled)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-gp", Name = ServiceId },
            Protocols = gpServerEnabled ? ["GPServer"] : ["FeatureServer"],
        };

    private sealed class ThrowingResourceValidator(Exception exception) : IResourceValidator
    {
        public Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
            string serviceId,
            CancellationToken cancellationToken = default)
            => throw exception;

        // Audit-C1: IResourceValidator V2 methods are now abstract on the interface,
        // so this throwing test stub must satisfy them explicitly. Only the
        // ValidateServiceV2Async path is exercised by these tests; the rest forward
        // the same simulated failure for completeness.
        public Task<ResourceValidationResult<MetadataV2Resource>> ValidateLayerV2Async(
            int layerId,
            CancellationToken cancellationToken = default)
            => throw exception;

        public Task<ResourceValidationResult<MetadataV2Resource>> ValidateCollectionV2Async(
            string collectionId,
            CancellationToken cancellationToken = default)
            => throw exception;

        public Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
            string serviceId,
            int layerId,
            CancellationToken cancellationToken = default)
            => throw exception;
    }
}
