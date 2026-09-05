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
using Honua.Protocols.Ogc.Api.Processes;
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
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_SucceededJob_StreamsStagedObject()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/tiff");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().Equal(_fixture.StagedPayload);
        response.Headers.ETag.Should().NotBeNull();
    }

    /// <summary>
    /// #3089 review: the checksum ETag flows through Results.Stream, so conditional
    /// requests (If-None-Match) are honoured with 304 instead of re-streaming.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_IfNoneMatchWithCurrentETag_Returns304()
    {
        var url =
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/artifacts/0/content";
        var first = await _fixture.App.Client.GetAsync(url);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag;
        etag.Should().NotBeNull();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(etag!);
        var second = await _fixture.App.Client.SendAsync(conditional);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_RunningJob_Returns404()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.RunningJobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_CancelledJob_NeverExposesStagedOutput()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.CancelledJobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_IndexOutOfRange_Returns404()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/artifacts/7/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_InvalidDurableDescriptor_FailsClosed()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.InvalidDescriptorJobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.ETag.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_DescriptorFromAnotherJob_FailsClosed()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.MismatchedDescriptorJobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_UnreconciledRegistrationIntent_FailsClosed()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.RegistrationPendingJobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsByteArrayAsync()).Should().NotEqual(_fixture.StagedPayload);
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
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactContentTestsFixture.SucceededJobId}/artifacts/0/content");

        // The link is a stable authenticated route, not a provider location.
        href.Should().NotContain("gp-outputs");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_StagedArtifactWithAdvertisedValueTransmission_ReturnsInlineValue()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactContentTestsFixture.ValueJobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var output = json.RootElement.GetProperty("outputRaster");
        output.TryGetProperty("href", out _).Should().BeFalse();
        output.GetProperty("mediaType").GetString().Should().Be("image/tiff");
        output.GetProperty("encoding").GetString().Should().Be("base64");
        Convert.FromBase64String(output.GetProperty("value").GetString()!).Should().Equal(_fixture.StagedPayload);
    }
}

/// <summary>
/// #3089 review (split-host staging config): when the serving host has NO matching
/// output store — worker-enabled/server-disabled or a mismatched StoreReference — the
/// results document must not advertise content links that are guaranteed to fail, and
/// the content route reports the store as unavailable. No descriptor internals leak.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesStagedArtifactStoreUnavailableTests
    : IClassFixture<OgcProcessesStagedArtifactStoreUnavailableTestsFixture>
{
    private readonly OgcProcessesStagedArtifactStoreUnavailableTestsFixture _fixture;

    public OgcProcessesStagedArtifactStoreUnavailableTests(
        OgcProcessesStagedArtifactStoreUnavailableTestsFixture fixture)
        => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_NoMatchingStore_DoesNotAdvertiseContentLink()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/ogc/processes/jobs/{OgcProcessesStagedArtifactStoreUnavailableTestsFixture.JobId}/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // No dead link, no descriptor internals, no payload.
        body.Should().NotContain("/api/geoprocessing/jobs/");
        body.Should().NotContain("gp-outputs");
        body.Should().NotContain("secret.tif");
        body.Should().NotContain("base64");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_NoMatchingStore_Returns503()
    {
        var response = await _fixture.App.Client.GetAsync(
            $"/api/geoprocessing/jobs/{OgcProcessesStagedArtifactStoreUnavailableTestsFixture.JobId}/artifacts/0/content");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}

/// <summary>
/// Fixture hosting the server WITHOUT a registered staged-output store while a
/// succeeded job references a staged artifact (the split-host misconfiguration).
/// </summary>
public sealed class OgcProcessesStagedArtifactStoreUnavailableTestsFixture : IAsyncLifetime
{
    public const string JobId = "gp-staged-storeless-001";

    public WebAppFixture App { get; }

    public OgcProcessesStagedArtifactStoreUnavailableTestsFixture()
    {
        var descriptor = new StagedObjectRasterOutputDescriptor
        {
            JobId = JobId,
            AttemptNumber = 1,
            OutputName = "outputRaster",
            Content = new RasterContentIdentity
            {
                SizeBytes = 1024,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('a', 64)),
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = CloudStorageProvider.Local,
            StoreReference = "gp-outputs",
            ObjectKey = $"gp/outputs/{JobId}/a1/outputRaster/secret.tif",
        };

        var now = DateTimeOffset.UtcNow;
        var succeeded = new ExecutionJobRecord
        {
            OperationId = JobId,
            Audit = new OperationAuditInfo { SubmitterSecurityContext = new(null, "public", []) },
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now,
            CompletedAt = now,
            AttemptCount = 1,
            ArtifactReferences = [RasterOutputJson.Serialize(descriptor)],
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test-backend",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "raster.resample"
            }
        };

        var mockJobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        mockJobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(succeeded);
        mockJobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);

        App = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(mockJobStore));
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}

/// <summary>
/// Per-class fixture hosting the server with a real filesystem staged-output store
/// and a substituted execution job store carrying one succeeded, one running, and one
/// cancelled geoprocessing job, all referencing the same staged object.
/// </summary>
public sealed class OgcProcessesStagedArtifactContentTestsFixture : IAsyncLifetime
{
    public const string SucceededJobId = "gp-staged-succeeded-001";
    public const string ValueJobId = "gp-staged-value-001";
    public const string RunningJobId = "gp-staged-running-001";
    public const string CancelledJobId = "gp-staged-cancelled-001";
    public const string RegistrationPendingJobId = "gp-staged-registration-pending-001";
    public const string InvalidDescriptorJobId = "gp-staged-invalid-descriptor-001";
    public const string MismatchedDescriptorJobId = "gp-staged-mismatched-descriptor-001";

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
        var valueObjectKey = GeoprocessingOutputObjectKeys.Build(
            stagingOptions.KeyPrefix, ValueJobId, attemptNumber: 1, "outputRaster", "result.tif");
        RasterContentIdentity valueContent;
        using (var payload = new MemoryStream(StagedPayload))
        {
            valueContent = store.WriteAsync(valueObjectKey, payload, "image/tiff").GetAwaiter().GetResult();
        }

        var valueReference = RasterOutputJson.Serialize(descriptor with
        {
            JobId = ValueJobId,
            Content = valueContent,
            ObjectKey = valueObjectKey,
        });
        var invalidDescriptorReference = RasterOutputJson.Serialize(descriptor with
        {
            JobId = InvalidDescriptorJobId,
            Content = descriptor.Content! with
            {
                Checksum = new RasterChecksum("sha256", "invalid")
            }
        });

        var succeeded = CreateJob(SucceededJobId, ExecutionJobStatus.Succeeded, reference);
        var value = CreateJob(
            ValueJobId,
            ExecutionJobStatus.Succeeded,
            valueReference,
            responseMode: "document");
        var running = CreateJob(RunningJobId, ExecutionJobStatus.Running, reference);
        var cancelled = CreateJob(CancelledJobId, ExecutionJobStatus.Cancelled, reference);
        var registrationPending = CreateJob(
            RegistrationPendingJobId,
            ExecutionJobStatus.Succeeded,
            reference,
            registrationTarget: "cog-catalog:7");
        var invalidDescriptor = CreateJob(
            InvalidDescriptorJobId, ExecutionJobStatus.Succeeded, invalidDescriptorReference);
        var mismatchedDescriptor = CreateJob(
            MismatchedDescriptorJobId, ExecutionJobStatus.Succeeded, reference);

        var mockJobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        mockJobStore.GetAsync(SucceededJobId, Arg.Any<CancellationToken>()).Returns(succeeded);
        mockJobStore.GetAsync(ValueJobId, Arg.Any<CancellationToken>()).Returns(value);
        mockJobStore.GetAsync(RunningJobId, Arg.Any<CancellationToken>()).Returns(running);
        mockJobStore.GetAsync(CancelledJobId, Arg.Any<CancellationToken>()).Returns(cancelled);
        mockJobStore.GetAsync(RegistrationPendingJobId, Arg.Any<CancellationToken>()).Returns(registrationPending);
        mockJobStore.GetAsync(InvalidDescriptorJobId, Arg.Any<CancellationToken>()).Returns(invalidDescriptor);
        mockJobStore.GetAsync(MismatchedDescriptorJobId, Arg.Any<CancellationToken>()).Returns(mismatchedDescriptor);
        mockJobStore.GetAsync(
                Arg.Is<string>(id => id != SucceededJobId
                    && id != ValueJobId
                    && id != RunningJobId
                     && id != CancelledJobId
                    && id != RegistrationPendingJobId
                    && id != InvalidDescriptorJobId
                    && id != MismatchedDescriptorJobId),
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

    private static ExecutionJobRecord CreateJob(
        string jobId,
        ExecutionJobStatus status,
        string reference,
        string? registrationTarget = null,
        string? responseMode = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Audit = new OperationAuditInfo { SubmitterSecurityContext = new(null, "public", []) },
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
                WorkloadName = "raster.resample",
                Parameters = CreateParameters(registrationTarget, responseMode)
            }
        };
    }

    private static Dictionary<string, string> CreateParameters(
        string? registrationTarget,
        string? responseMode)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (registrationTarget is not null)
        {
            parameters["honua.geoprocessing.output_registration.outputRaster"] = registrationTarget;
        }

        if (responseMode is not null)
        {
            parameters[OgcProcessesExecutionMetadata.ResponseMode] = responseMode;
            parameters["process.output.0"] = "outputRaster";
        }

        return parameters;
    }
}
