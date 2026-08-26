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
            """{"inputs":{"source":"AAAA","units":"degrees"}}""",
            Encoding.UTF8,
            "application/json");

        using var response = await _fixture.App.Client.PostAsync(
            "/ogc/processes/processes/surface.slope/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Contains("Preference-Applied").Should().BeFalse(
            "no client preference was supplied");
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
}

public sealed class OgcProcessesSynchronousExecutionFixture : IAsyncLifetime
{
    private const string JobId = "ogc-sync-result-job";

    public WebAppFixture App { get; }

    public AnalysisPlan? SubmittedPlan { get; private set; }

    public OgcProcessesSynchronousExecutionFixture()
    {
        var job = CreateJob();
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
            .Returns(callInfo =>
            {
                SubmittedPlan = callInfo.ArgAt<AnalysisPlan>(0);
                return Task.FromResult(job);
            });

        var terminalService = Substitute.For<IGeoprocessingJobTerminalService>();
        terminalService.WaitForResultAsync(
                JobId,
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeoprocessingTerminalResult(
                GeoprocessingTerminalResultOutcome.Succeeded,
                job,
                CreateResultPackage()));

        App = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobService);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(terminalService);
        });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private static ExecutionJobRecord CreateJob()
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
                WorkloadName = "geometry-buffer"
            }
        };

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
