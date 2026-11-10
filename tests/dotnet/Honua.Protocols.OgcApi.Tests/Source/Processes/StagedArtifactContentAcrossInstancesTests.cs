// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
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

// Two server instances over one bucket: the instance that staged an output is not the one that
// serves it, which is what two tasks behind a load balancer look like.
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class StagedArtifactContentAcrossInstancesTests
    : IClassFixture<StagedArtifactContentAcrossInstancesFixture>
{
    private readonly StagedArtifactContentAcrossInstancesFixture _fixture;

    public StagedArtifactContentAcrossInstancesTests(StagedArtifactContentAcrossInstancesFixture fixture)
        => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_StagedByOneInstance_IsServedWholeByTheOther()
    {
        var response = await _fixture.ServingInstance.Client.GetAsync(StagedArtifactContentAcrossInstancesFixture.ContentUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/tiff");
        response.Content.Headers.ContentLength.Should().Be(_fixture.StagedPayload.Length);
        response.Headers.ETag!.Tag.Should().Be($"\"sha256:{_fixture.Checksum}\"");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(_fixture.StagedPayload);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_RangeRequestToTheOtherInstance_ReturnsThatRange()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, StagedArtifactContentAcrossInstancesFixture.ContentUrl);
        request.Headers.Range = new RangeHeaderValue(20_000, 20_999);

        var response = await _fixture.ServingInstance.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.From.Should().Be(20_000);
        response.Content.Headers.ContentRange.To.Should().Be(20_999);
        response.Content.Headers.ContentRange.Length.Should().Be(_fixture.StagedPayload.Length);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(_fixture.StagedPayload.AsSpan(20_000, 1_000).ToArray());
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_IfNoneMatchOnTheOtherInstance_Returns304()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, StagedArtifactContentAcrossInstancesFixture.ContentUrl);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"sha256:{_fixture.Checksum}\""));

        var response = await _fixture.ServingInstance.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content")]
    public async Task ArtifactContent_AfterTheObjectIsDeleted_Returns404OnBothInstances()
    {
        await _fixture.DeleteStagedObjectAsync();

        var fromServing = await _fixture.ServingInstance.Client.GetAsync(StagedArtifactContentAcrossInstancesFixture.DeletedContentUrl);
        var fromStaging = await _fixture.StagingInstance.Client.GetAsync(StagedArtifactContentAcrossInstancesFixture.DeletedContentUrl);

        fromServing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fromStaging.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class StagedArtifactContentAcrossInstancesFixture : IAsyncLifetime
{
    public const string JobId = "gp-staged-shared-bucket-001";
    public const string DeletedJobId = "gp-staged-shared-bucket-deleted-001";
    public const string ContentUrl = $"/api/geoprocessing/jobs/{JobId}/artifacts/0/content";
    public const string DeletedContentUrl = $"/api/geoprocessing/jobs/{DeletedJobId}/artifacts/0/content";

    private readonly SharedBucket _bucket = new();
    private readonly GeoprocessingOutputStagingOptions _options = new()
    {
        Enabled = true,
        Provider = GeoprocessingOutputStagingOptions.ObjectStorageProvider,
    };

    private readonly string _deletedObjectKey;

    public StagedArtifactContentAcrossInstancesFixture()
    {
        var stagingStore = CreateStore();
        var reference = Stage(stagingStore, JobId, out var content, out _);
        Checksum = content.Checksum!.Value;
        var deletedReference = Stage(stagingStore, DeletedJobId, out _, out _deletedObjectKey);

        var jobs = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobs.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(SucceededJob(JobId, reference));
        jobs.GetAsync(DeletedJobId, Arg.Any<CancellationToken>()).Returns(SucceededJob(DeletedJobId, deletedReference));

        StagingInstance = CreateInstance(jobs, stagingStore);
        ServingInstance = CreateInstance(jobs, CreateStore());
    }

    public WebAppFixture StagingInstance { get; }

    public WebAppFixture ServingInstance { get; }

    public byte[] StagedPayload { get; } = Enumerable.Range(0, 32768).Select(index => (byte)((index * 31 + 7) % 256)).ToArray();

    public string Checksum { get; }

    public Task<bool> DeleteStagedObjectAsync() => CreateStore().DeleteAsync(_deletedObjectKey);

    public async Task InitializeAsync()
    {
        await StagingInstance.InitializeAsync();
        await ServingInstance.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ServingInstance.DisposeAsync();
        await StagingInstance.DisposeAsync();
    }

    private ObjectStorageGeoprocessingOutputObjectStore CreateStore()
        => new(
            new SharedBucketCloudFileStorage(_bucket),
            Options.Create(_options),
            new GeoprocessingOutputStorageKeyPrefix(string.Empty));

    private string Stage(
        ObjectStorageGeoprocessingOutputObjectStore store,
        string jobId,
        out RasterContentIdentity content,
        out string objectKey)
    {
        objectKey = GeoprocessingOutputObjectKeys.Build(_options.KeyPrefix, jobId, attemptNumber: 1, "outputRaster", "result.tif");
        using var payload = new MemoryStream(StagedPayload);
        content = store.WriteAsync(objectKey, payload, "image/tiff").GetAwaiter().GetResult();
        return RasterOutputJson.Serialize(new StagedObjectRasterOutputDescriptor
        {
            JobId = jobId,
            AttemptNumber = 1,
            OutputName = "outputRaster",
            Content = content,
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = store.Provider,
            StoreReference = store.StoreReference,
            ObjectKey = objectKey,
        });
    }

    private static WebAppFixture CreateInstance(IExecutionJobStore jobs, IGeoprocessingOutputObjectStore store)
        => new WebAppFixture().ConfigureServices(services =>
        {
            services.AddSingleton(jobs);
            services.AddSingleton(store);
        });

    private static ExecutionJobRecord SucceededJob(string jobId, string reference)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Audit = new OperationAuditInfo
            {
                SubmitterSecurityContext = new JobSecurityContext(null, "public", []),
            },
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now,
            CompletedAt = now,
            AttemptCount = 1,
            ArtifactReferences = [reference],
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test-backend",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "raster.resample",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        };
    }
}
