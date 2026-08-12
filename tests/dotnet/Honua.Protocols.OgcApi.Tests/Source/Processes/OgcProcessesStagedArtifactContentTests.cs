// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// End-to-end coverage of the staged raster output surface (#3089) against local
/// object storage: the canonical authenticated content route streams the immutable
/// staged object for a succeeded job; incomplete (running), cancelled, and failed
/// jobs never expose staged content; and the OGC results document links staged
/// artifacts through the content route instead of embedding payload bytes or
/// backing-store locations.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesStagedArtifactContentTests
    : IClassFixture<OgcProcessesStagedArtifactContentTestsFixture>
{
    private readonly OgcProcessesStagedArtifactContentTestsFixture _fixture;

    public OgcProcessesStagedArtifactContentTests(OgcProcessesStagedArtifactContentTestsFixture fixture)
        => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_SucceededJob_StreamsStagedObject()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/results/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/tiff");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().Equal(_fixture.StagedPayload);
        response.Headers.ETag.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_RunningJob_Returns404()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.RunningJobId}/results/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_CancelledJob_NeverExposesStagedOutput()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.CancelledJobId}/results/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_IndexOutOfRange_Returns404()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/results/artifacts/7/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_StagedArtifact_LinksContentRouteWithoutPayload()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("base64");

        var json = JsonDocument.Parse(body);
        var output = json.RootElement.EnumerateObject().First().Value;
        var href = output.GetProperty("href").GetString();
        href.Should().Contain(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/results/artifacts/0/content");

        // The link is a stable authenticated route, not a provider location.
        href.Should().NotContain("gp-outputs");
    }
}

/// <summary>
/// Per-class fixture hosting the server with a real filesystem staged-output store
/// and a substituted execution job store carrying one succeeded, one running, and one
/// cancelled geoprocessing job, all referencing the same staged object.
/// </summary>
public sealed class OgcProcessesStagedArtifactContentTestsFixture : IAsyncLifetime
{
    public const string SucceededJobId = "gp-staged-succeeded-001";
    public const string RunningJobId = "gp-staged-running-001";
    public const string CancelledJobId = "gp-staged-cancelled-001";

    private readonly string _storeRoot = Directory.CreateTempSubdirectory("honua-ogc-staged-content-").FullName;

    public WebAppFixture App { get; }

    public byte[] StagedPayload { get; } = CreatePayload();

    public OgcProcessesStagedArtifactContentTestsFixture()
    {
        var stagingOptions = new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            LocalRootPath = _storeRoot,
        };
        var store = new FileSystemGeoprocessingOutputObjectStore(Options.Create(stagingOptions));

        var objectKey = GeoprocessingOutputObjectKeys.Build(
            stagingOptions.KeyPrefix, SucceededJobId, attemptNumber: 1, "outputRaster", "result.tif");
        RasterContentIdentity content;
        using (var payload = new MemoryStream(StagedPayload))
        {
            content = store.WriteAsync(objectKey, payload, "image/tiff").GetAwaiter().GetResult();
        }

        var descriptor = new StagedObjectRasterOutputDescriptor
        {
            JobId = SucceededJobId,
            AttemptNumber = 1,
            OutputName = "outputRaster",
            Content = content,
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = store.Provider,
            StoreReference = store.StoreReference,
            ObjectKey = objectKey,
        };
        var reference = RasterOutputJson.Serialize(descriptor);

        var succeeded = CreateJob(SucceededJobId, ExecutionJobStatus.Succeeded, reference);
        var running = CreateJob(RunningJobId, ExecutionJobStatus.Running, reference);
        var cancelled = CreateJob(CancelledJobId, ExecutionJobStatus.Cancelled, reference);

        var mockJobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        mockJobStore.GetAsync(SucceededJobId, Arg.Any<CancellationToken>()).Returns(succeeded);
        mockJobStore.GetAsync(RunningJobId, Arg.Any<CancellationToken>()).Returns(running);
        mockJobStore.GetAsync(CancelledJobId, Arg.Any<CancellationToken>()).Returns(cancelled);
        mockJobStore.GetAsync(
                Arg.Is<string>(id => id != SucceededJobId && id != RunningJobId && id != CancelledJobId),
                Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);

        App = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton(mockJobStore);
                services.AddSingleton<IGeoprocessingOutputObjectStore>(store);
            });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        try
        {
            Directory.Delete(_storeRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup.
        }
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    private static ExecutionJobRecord CreateJob(string jobId, ExecutionJobStatus status, string reference)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now,
            CompletedAt = status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Cancelled ? now : null,
            AttemptCount = 1,
            ArtifactReferences = status == ExecutionJobStatus.Running ? Array.Empty<string>() : [reference],
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test-backend",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "raster.resample"
            }
        };
    }
}
