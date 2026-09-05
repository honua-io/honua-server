// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesSynchronousExecutionTests : IClassFixture<OgcProcessesSynchronousExecutionFixture>
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly OgcProcessesSynchronousExecutionFixture _fixture;

    public OgcProcessesSynchronousExecutionTests(OgcProcessesSynchronousExecutionFixture fixture)
        => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_OmittedPreferRawGeoJson_ReturnsInlineValueAndSubmitsCanonicalWkb()
    {
        const string body = """
            {
              "inputs": {
                "wkb": {
                  "type": "Feature",
                  "value": "foreign-metadata",
                  "geometry": { "type": "Point", "coordinates": [1, 2] },
                  "properties": {}
                },
                "srid": 4326,
                "distance": 25.5
              },
              "response": "raw"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _fixture.App.Client.PostAsync(
            "/ogc/processes/processes/geometry.buffer/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"value\":42}");
        response.Headers.Contains("Preference-Applied").Should().BeFalse();

        _fixture.SubmittedPlan.Should().NotBeNull();
        var submitted = _fixture.SubmittedPlan!;
        var encodedWkb = submitted.Steps.Single().Inputs["wkb"];
        var geometry = new WKBReader().Read(Convert.FromBase64String(encodedWkb));
        geometry.GeometryType.Should().Be("Point");
        geometry.SRID.Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_OmittedPreferForAsyncOnlyProcess_ReturnsAsyncWithoutAcknowledgement()
    {
        using var content = new StringContent(
            """
            {
              "inputs": {
                "plan": {
                  "planId": "async-plan",
                  "steps": [
                    {
                      "stepId": "s1",
                      "kind": "geoprocess",
                      "processId": "surface.slope",
                      "inputs": { "source": "AAAA", "units": "degrees" }
                    }
                  ]
                }
              }
            }
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await _fixture.App.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Contains("Preference-Applied").Should().BeFalse(
            "no client preference was supplied");
        _fixture.SubmittedMetadata.Should().NotContainKey("ogc.processes.response");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_Base64WkbInput_PreservesExistingCanonicalRepresentation()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}",
            Encoding.UTF8,
            "application/json");

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _fixture.SubmittedPlan!.Steps.Single().Inputs["wkb"].Should().Be(PointWkbBase64);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_CanonicalPlanRaw_RejectsBeforeSubmission()
    {
        var submissions = _fixture.SubmissionCount;
        using var content = new StringContent(
            """{"inputs":{"plan":{"planId":"raw-plan","steps":[{"stepId":"s1","kind":"geoprocess","processId":"surface.slope","inputs":{"source":"AAAA"}}]}},"response":"raw"}""",
            Encoding.UTF8, "application/json");
        using var response = await _fixture.App.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("requires document mode");
        _fixture.SubmissionCount.Should().Be(submissions);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_AsyncRawCatalogProcess_IsAdmittedAndPersistsResponseMode()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}},\"response\":\"raw\"}}",
            Encoding.UTF8,
            "application/json");

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "Part 1 applies raw response negotiation to asynchronous results too (#4145)");
        _fixture.SubmittedMetadata!["ogc.processes.response"].Should().Be("raw");

        using var asyncOnlyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/surface.slope/execution");
        asyncOnlyRequest.Content = new StringContent(
            """{"inputs":{"source":"AAAA"},"response":"raw"}""",
            Encoding.UTF8,
            "application/json");

        using var asyncOnlyResponse = await _fixture.App.Client.SendAsync(asyncOnlyRequest);

        asyncOnlyResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            "raw must also be admitted when the catalog process has no synchronous mode (#4145)");
        _fixture.SubmittedMetadata!["ogc.processes.response"].Should().Be("raw");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_ReferenceWithoutMediaType_PreserveNumericTextAndBinaryWkb()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"wkb":{"href":"https://93.184.216.34/point.wkb"},"srid":4326,"distance":{"href":"https://93.184.216.34/distance"}}}""",
            Encoding.UTF8, "application/json");
        using var response = await _fixture.App.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _fixture.SubmittedPlan!.Steps.Single().Inputs["distance"].Should().Be("25.5");
        _fixture.SubmittedPlan!.Steps.Single().Inputs["wkb"].Should().Be(PointWkbBase64);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_QualifiedAndReferencedCatalogInputs_AreNormalized()
    {
        const string geoJson = "{\"type\":\"Point\",\"coordinates\":[1,2]}";
        var href = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            $$"""
            {
              "inputs": {
                "wkb": { "href": "{{href}}", "type": "application/geo+json" },
                "srid": { "value": 4326 },
                "distance": { "value": 25.5 }
              }
            }
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "catalog processes must accept the same qualified and by-reference input forms as CITE (#4146)");
        var inputs = _fixture.SubmittedPlan!.Steps.Single().Inputs;
        inputs["srid"].Should().Be("4326");
        inputs["distance"].Should().Be("25.5");
        new WKBReader().Read(Convert.FromBase64String(inputs["wkb"])).GeometryType.Should().Be("Point");
        AssertSubmittedPlanIsValid();
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_QualifiedWkbAndHttpsReference_ProduceValidCatalogPlans()
    {
        var geometries = new[]
        {
            $$"""{"value":"{{PointWkbBase64}}","mediaType":"application/wkb"}""",
            """{"value":{"type":"Point","coordinates":[1,2]},"mediaType":"application/geo+json"}""",
            """{"href":"https://93.184.216.34/point.geojson","type":"application/geo+json"}"""
        };

        foreach (var geometry in geometries)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "/ogc/processes/processes/geometry.buffer/execution");
            request.Headers.Add("Prefer", "respond-async");
            request.Content = new StringContent(
                $$$$"""{"inputs":{"wkb":{{{{geometry}}}},"srid":{"value":4326},"distance":{"value":25.5}}}""",
                Encoding.UTF8, "application/json");

            using var response = await _fixture.App.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            AssertSubmittedPlanIsValid();
            new WKBReader().Read(Convert.FromBase64String(_fixture.SubmittedPlan!.Steps.Single().Inputs["wkb"]))
                .GeometryType.Should().Be("Point");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnsafeInputReferences_AreRejectedBeforeSubmission()
    {
        foreach (var href in new[] { "https://127.0.0.1/secret", "https://169.254.169.254/", "file:///C:/secret", "data:application/json;base64,!!!" })
        {
            var submissions = _fixture.SubmissionCount;
            using var content = new StringContent(
                $$$$"""{"inputs":{"wkb":{"href":"{{{{href}}}}"},"srid":4326,"distance":25.5}}""",
                Encoding.UTF8, "application/json");
            using var response = await _fixture.App.Client.PostAsync(
                "/ogc/processes/processes/geometry.buffer/execution", content);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _fixture.SubmissionCount.Should().Be(submissions);
        }
    }

    private void AssertSubmittedPlanIsValid()
    {
        var catalog = _fixture.App.Services.GetRequiredService<IProcessCatalog>();
        ProcessPlanValidator.Validate(_fixture.SubmittedPlan!, catalog).Violations.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnknownInput_DoesNotResolveAnyReferences()
    {
        var requestsBefore = _fixture.ReferenceRequestCount;
        var submissionsBefore = _fixture.SubmissionCount;
        using var content = new StringContent(
            """{"inputs":{"wkb":{"href":"https://93.184.216.34/point.geojson"},"srid":4326,"distance":25.5,"unknown":{"href":"https://93.184.216.34/point.geojson"}}}""",
            Encoding.UTF8, "application/json");
        using var response = await _fixture.App.Client.PostAsync("/ogc/processes/processes/geometry.buffer/execution", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fixture.ReferenceRequestCount.Should().Be(requestsBefore);
        _fixture.SubmissionCount.Should().Be(submissionsBefore);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_ReferencedInputs_EnforcesAggregateByteLimit()
    {
        var payload = "{\"type\":\"FeatureCollection\",\"features\":[],\"padding\":\"" + new string('x', 700) + "\"}";
        var href = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var body = JsonSerializer.Serialize(new { inputs = new { input = new { href }, clip = new { href } } });
        var submissionsBefore = _fixture.SubmissionCount;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _fixture.App.Client.PostAsync("/ogc/processes/processes/overlay.clip/execution", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("exceeds");
        _fixture.SubmissionCount.Should().Be(submissionsBefore);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_InvalidQualifiedGeometry_IsRejectedBeforeSubmission()
    {
        foreach (var geometry in new[]
        {
            """{"value":{"type":"Point","coordinates":[1,2]},"mediaType":"text/plain"}""",
            """{"value":null,"mediaType":"application/geo+json"}"""
        })
        {
            var submissions = _fixture.SubmissionCount;
            using var content = new StringContent(
                $$$$"""{"inputs":{"wkb":{{{{geometry}}}},"srid":4326,"distance":25.5}}""",
                Encoding.UTF8, "application/json");
            using var response = await _fixture.App.Client.PostAsync(
                "/ogc/processes/processes/geometry.buffer/execution", content);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _fixture.SubmissionCount.Should().Be(submissions);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task ReferenceDocumentation_ExecuteExample_IsRunnableVerbatim()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        var documentation = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "docs", "reference", "protocols", "ogc-apis.md"));
        const string marker = "In the [API explorer]";
        var exampleStart = documentation.IndexOf(marker, StringComparison.Ordinal);
        exampleStart.Should().BeGreaterThanOrEqualTo(0);
        var methodStart = documentation.IndexOf("`POST ", exampleStart, StringComparison.Ordinal) + 6;
        var methodEnd = documentation.IndexOf('`', methodStart);
        var endpoint = documentation[methodStart..methodEnd];
        var jsonStart = documentation.IndexOf("```json", methodEnd, StringComparison.Ordinal) + 7;
        var jsonEnd = documentation.IndexOf("```", jsonStart, StringComparison.Ordinal);
        var body = documentation[jsonStart..jsonEnd].Trim();
        body.Should().NotContain("<", "the only execute example must not contain a placeholder (#4150)");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _fixture.App.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the reference documentation's only execute request must run verbatim (#4150)");
        AssertSubmittedPlanIsValid();
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_MalformedNestedGeoJson_ReturnsSanitizedValidationError()
    {
        var malformedInputs = new[]
        {
            """{"type":"Feature","geometry":[]}""",
            """{"type":"Feature","geometry":{}}""",
            """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"coordinates":[0,0]}}]}"""
        };

        foreach (var geoJson in malformedInputs)
        {
            using var content = new StringContent(
                "{\"inputs\":{\"wkb\":" + geoJson + ",\"srid\":4326,\"distance\":25.5}}",
                Encoding.UTF8,
                "application/json");

            using var response = await _fixture.App.Client.PostAsync(
                "/ogc/processes/processes/geometry.buffer/execution",
                content);

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "malformed nested GeoJSON {0} must be rejected",
                geoJson);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            error.RootElement.GetProperty("title").GetString().Should().Be("Invalid analysis plan");
            error.RootElement.GetProperty("detail").GetString().Should().Contain("valid GeoJSON geometry");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_EmptyFeatureCollection_ReturnsSanitizedValidationErrorWithoutSubmittingJob()
    {
        foreach (var preferAsync in new[] { false, true })
        {
            var submissionsBeforeRequest = _fixture.SubmissionCount;
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/ogc/processes/processes/geometry.buffer/execution");
            if (preferAsync)
            {
                request.Headers.Add("Prefer", "respond-async");
            }

            request.Content = new StringContent(
                """{"inputs":{"wkb":{"type":"FeatureCollection","features":[]},"srid":4326,"distance":25.5}}""",
                Encoding.UTF8,
                "application/json");

            using var response = await _fixture.App.Client.SendAsync(request);

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "an empty FeatureCollection must be rejected before {0} submission",
                preferAsync ? "asynchronous" : "synchronous");
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            error.RootElement.GetProperty("title").GetString().Should().Be("Invalid analysis plan");
            error.RootElement.GetProperty("detail").GetString().Should().Contain("valid GeoJSON geometry");
            error.RootElement.GetProperty("detail").GetString().Should().NotContain("FeatureCollection");
            _fixture.SubmissionCount.Should().Be(submissionsBeforeRequest);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_SynchronousFailure_ReturnsRegisteredJobFailedException()
    {
        _fixture.SetTerminalFailure("Worker rejected the input.");
        try
        {
            using var content = new StringContent(
                $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}",
                Encoding.UTF8,
                "application/json");

            using var response = await _fixture.App.Client.PostAsync(
                "/ogc/processes/processes/geometry.buffer/execution",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            error.RootElement.GetProperty("type").GetString().Should().Be(
                "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/job-failed");
            error.RootElement.GetProperty("detail").GetString().Should().Be("Worker rejected the input.");
        }
        finally
        {
            _fixture.ResetTerminalResult();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_SynchronousCancellation_ReturnsRegisteredJobDismissedException()
    {
        _fixture.SetTerminalCancellation();
        try
        {
            using var content = new StringContent(
                $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}",
                Encoding.UTF8,
                "application/json");

            using var response = await _fixture.App.Client.PostAsync(
                "/ogc/processes/processes/geometry.buffer/execution",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.Gone);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            error.RootElement.GetProperty("type").GetString().Should().Be(
                "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/job-dismissed");
            error.RootElement.GetProperty("status").GetInt32().Should().Be(410);
        }
        finally
        {
            _fixture.ResetTerminalResult();
        }
    }
}

public sealed class OgcProcessesSynchronousExecutionFixture : IAsyncLifetime
{
    private const string JobId = "ogc-sync-result-job";
    private GeoprocessingTerminalResult _terminalResult;

    public WebAppFixture App { get; }

    public AnalysisPlan? SubmittedPlan { get; private set; }

    public IReadOnlyDictionary<string, string>? SubmittedMetadata { get; private set; }

    public int SubmissionCount { get; private set; }

    public int ReferenceRequestCount { get; private set; }

    public OgcProcessesSynchronousExecutionFixture()
    {
        var job = CreateJob();
        _terminalResult = CreateSucceededTerminalResult(job);
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        jobService.SubmitJobAsync(
                Arg.Any<AnalysisPlan>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => RecordSubmission(
                callInfo.ArgAt<AnalysisPlan>(0),
                callInfo.ArgAt<IReadOnlyDictionary<string, string>?>(3)));

        // The processes adapter submits through SubmitProtocolJobAsync, a default
        // interface member whose default body forwards to SubmitJobAsync. NSubstitute
        // intercepts default members rather than running that body, so the substitute
        // returns null unless the adapter-scoped overload is stubbed too (#3584).
        jobService.SubmitProtocolJobAsync(
                Arg.Any<AnalysisPlan>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IProcessCatalog>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => RecordSubmission(
                callInfo.ArgAt<AnalysisPlan>(0),
                callInfo.ArgAt<IReadOnlyDictionary<string, string>?>(4)));

        var terminalService = Substitute.For<IGeoprocessingJobTerminalService>();
        terminalService.WaitForResultAsync(
                JobId,
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => _terminalResult);

        App = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobService);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(terminalService);
            services.AddHttpClient(OgcProcessInputReferenceHttpClient.Name)
                .ConfigurePrimaryHttpMessageHandler(() => new InputReferenceHandler(() => ReferenceRequestCount++));
            services.Configure<GeoprocessingExecutorOptions>(options => options.MaxArtifactBytes = 1024);
        });

        Task<ExecutionJobRecord> RecordSubmission(
            AnalysisPlan plan,
            IReadOnlyDictionary<string, string>? metadata)
        {
            SubmittedPlan = plan;
            SubmittedMetadata = metadata;
            SubmissionCount++;
            return Task.FromResult(job);
        }
    }

    public Task InitializeAsync() => App.InitializeAsync();

    private sealed class InputReferenceHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            if (request.RequestUri!.AbsolutePath == "/point.wkb")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Convert.FromBase64String("AQEAAAAAAAAAAAAAAAAAAAAAAAAA"))
                });
            }

            if (request.RequestUri!.AbsolutePath == "/distance")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("25.5"))
                });
            }

            request.RequestUri!.AbsoluteUri.Should().Be("https://93.184.216.34/point.geojson");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"type":"Point","coordinates":[1,2]}""", Encoding.UTF8, "application/geo+json")
            });
        }
    }

    public Task DisposeAsync() => App.DisposeAsync();

    public void SetTerminalFailure(string message)
    {
        var failedJob = CreateJob(ExecutionJobStatus.Failed, message);
        _terminalResult = new GeoprocessingTerminalResult(
            GeoprocessingTerminalResultOutcome.Failed,
            failedJob);
    }

    public void SetTerminalCancellation()
    {
        var cancelledJob = CreateJob(ExecutionJobStatus.Cancelled);
        _terminalResult = new GeoprocessingTerminalResult(
            GeoprocessingTerminalResultOutcome.Cancelled,
            cancelledJob);
    }

    public void ResetTerminalResult()
    {
        var succeededJob = CreateJob();
        _terminalResult = CreateSucceededTerminalResult(succeededJob);
    }

    private static ExecutionJobRecord CreateJob(
        ExecutionJobStatus status = ExecutionJobStatus.Succeeded,
        string? errorMessage = null)
        => new()
        {
            OperationId = JobId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geometry-buffer"
            }
        };

    private static GeoprocessingTerminalResult CreateSucceededTerminalResult(ExecutionJobRecord job)
        => new(
            GeoprocessingTerminalResultOutcome.Succeeded,
            job,
            CreateResultPackage());

    private static AnalysisResultPackage CreateResultPackage()
        => AnalysisResultPackage.CreateCompleted(
            $"{JobId}:v1",
            new ResultSummary { Title = "Raw scalar" },
            [
                new ArtifactRef
                {
                    ArtifactId = "raw-value",
                    Kind = ArtifactKind.Scalar,
                    Label = "value",
                    Uri = "data:application/json;base64,eyJ2YWx1ZSI6NDJ9",
                    ContentType = "application/json"
                }
            ],
            [],
            new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"],
                ExecutedAt = DateTimeOffset.UtcNow
            });
}
