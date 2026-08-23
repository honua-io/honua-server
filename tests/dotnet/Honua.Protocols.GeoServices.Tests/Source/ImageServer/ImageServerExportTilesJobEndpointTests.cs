// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Direct endpoint coverage for the asynchronous (Compact Cache V2 / TPKX) ImageServer exportTiles
/// job lifecycle: submission, status projection, validation, and sanitized not-found scoping.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerExportTilesJobEndpointTests
{
    private const int TestLayerId = 0;

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    [Endpoint("GET /rest/services/{id}/ImageServer/jobs/{jobId}")]
    public async Task ExportTiles_CompactV2_SubmitsDurableJobAndProjectsStatus()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var submit = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?f=json" +
                "&storageFormatType=esriMapCacheStorageModeCompactV2" +
                "&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0,1,2&format=png&maxTiles=1000");

            var submitBody = await submit.Content.ReadAsStringAsync();
            submitBody.Should().Contain("esriJobSubmitted", $"submit response was: {submitBody}");
            var submitted = JsonSerializer.Deserialize(
                submitBody,
                ImageServerJsonContext.Default.ImageServerExportTilesJobSubmitResponse);
            submitted!.JobStatus.Should().Be("esriJobSubmitted");
            submitted.JobId.Should().NotBeNullOrEmpty();

            var status = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/{submitted.JobId}");
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            var statusBody = JsonSerializer.Deserialize(
                await status.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesJobStatusResponse);
            statusBody!.JobId.Should().Be(submitted.JobId);
            statusBody.JobStatus.Should().Be("esriJobSubmitted");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    public async Task ExportTiles_CompactV2_SingleZoomLevel_ReturnsError()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?f=json" +
                "&storageFormatType=esriMapCacheStorageModeCompactV2" +
                "&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0&format=png&maxTiles=1000");

            // The GeoServices error envelope is delivered with an error body; the durable path must
            // reject a single-level TPKX request rather than submitting a job.
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("esriJobSubmitted");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{id}/ImageServer/jobs/{jobId}/cancel")]
    [Endpoint("GET /rest/services/{id}/ImageServer/jobs/{jobId}/results/out_service_url")]
    public async Task ExportTiles_CancelAndResult_NumericLayer()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var jobId = await SubmitCompactJobAsync(fixture, $"/rest/services/{TestLayerId}/ImageServer/exportTiles");

            var cancel = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/{jobId}/cancel", content: null);
            var cancelBody = await cancel.Content.ReadAsStringAsync();
            cancelBody.Should().Contain("esriJobCancelled");

            // A cancelled (non-succeeded) job has no result package; the route surfaces the sanitized
            // precondition rather than a URL.
            var result = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/{jobId}/results/out_service_url");
            var resultBody = await result.Content.ReadAsStringAsync();
            resultBody.Should().NotContain("out_service_url\":{\"value");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/jobs/{jobId}")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/jobs/{jobId}/cancel")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/jobs/{jobId}/results/out_service_url")]
    public async Task ExportTiles_SubmitStatusCancelResult_ByService()
    {
        var fixture = await CreateDurableFixtureAsync();
        var serviceId = WebAppFixture.TestServiceId;
        try
        {
            var jobId = await SubmitCompactJobAsync(fixture, $"/rest/services/{serviceId}/ImageServer/exportTiles");

            var status = await fixture.Client.GetAsync($"/rest/services/{serviceId}/ImageServer/jobs/{jobId}");
            (await status.Content.ReadAsStringAsync()).Should().Contain(jobId);

            var cancel = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/jobs/{jobId}/cancel", content: null);
            (await cancel.Content.ReadAsStringAsync()).Should().Contain("esriJobCancelled");

            var result = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/jobs/{jobId}/results/out_service_url");
            (await result.Content.ReadAsStringAsync()).Should().NotContain("out_service_url\":{\"value");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/jobs/{jobId}")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/jobs/{jobId}/cancel")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/jobs/{jobId}/results/out_service_url")]
    public async Task ExportTiles_JobLifecycleByService_PreservesResolvedPublication()
    {
        const string serviceId = "z-aliased-image";
        const string otherServiceId = "y-other-image";
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var restricted = new AccessPolicy { AllowedRoles = ["imagery-admin"] };
        var graph = new TestMetadataV2GraphBuilder()
                .AddResource(
                    "competing-resource",
                    "Competing image",
                    MetadataV2ResourceType.RasterDataset,
                    accessPolicy: restricted)
                .AddStorageBinding(
                    "competing-binding",
                    "competing-resource",
                    "competing.rasters",
                    storageLayerId: TestLayerId)
                .AddService(
                    "competing-service",
                    "a-competing-image",
                    protocols: [ServiceProtocols.ImageServer],
                    accessPolicy: restricted)
                .AddPublication(
                    "competing-publication",
                    "competing-service",
                    "competing-resource",
                    layerIndex: 7,
                    storageBindingId: "competing-binding",
                    publicationType: MetadataV2PublicationType.EsriImageLayer,
                    isPrimary: true)
                .AddResource(
                    "target-resource",
                    "Target image",
                    MetadataV2ResourceType.RasterDataset,
                    accessPolicy: anonymous)
                .AddStorageBinding(
                    "target-binding",
                    "target-resource",
                    "target.rasters",
                    storageLayerId: TestLayerId)
                .AddService(
                    "target-service",
                    serviceId,
                    protocols: [ServiceProtocols.ImageServer],
                    accessPolicy: anonymous)
                .AddPublication(
                    "target-publication",
                    "target-service",
                    "target-resource",
                    layerIndex: 41,
                    storageBindingId: "target-binding",
                    publicationType: MetadataV2PublicationType.EsriImageLayer)
                .AddResource(
                    "other-resource",
                    "Other image",
                    MetadataV2ResourceType.RasterDataset,
                    accessPolicy: anonymous)
                .AddStorageBinding(
                    "other-binding",
                    "other-resource",
                    "other.rasters",
                    storageLayerId: TestLayerId)
                .AddService(
                    "other-service",
                    otherServiceId,
                    protocols: [ServiceProtocols.ImageServer],
                    accessPolicy: anonymous)
                .AddPublication(
                    "other-publication",
                    "other-service",
                    "other-resource",
                    layerIndex: 42,
                    storageBindingId: "other-binding",
                    publicationType: MetadataV2PublicationType.EsriImageLayer)
                .Build();
        var graphProvider = new TestMetadataV2GraphProvider(graph);
        var resolver = Substitute.For<IImageServerLayerResolver>();
        var targetStorageLayerId = TestLayerId;
        resolver.ResolveFirstAccessibleLayerAsync(
                Arg.Any<string>(),
                Arg.Any<HttpContext>(),
                AuthorizationOperation.Export,
                Arg.Any<CancellationToken>())
            .Returns(callInfo => string.Equals(callInfo.ArgAt<string>(0), otherServiceId, StringComparison.Ordinal)
                ? new ImageServerLayerResolution(
                    TestLayerId,
                    "other-publication",
                    42,
                    ErrorResult: null)
                : new ImageServerLayerResolution(
                    targetStorageLayerId,
                    "target-publication",
                    41,
                    ErrorResult: null));
        resolver.ValidateLayerAsync(
                TestLayerId,
                Arg.Any<HttpContext>(),
                AuthorizationOperation.Export,
                Arg.Any<CancellationToken>())
            .Returns(new ImageServerLayerResolution(
                TestLayerId,
                "competing-publication",
                7,
                Results.StatusCode(StatusCodes.Status403Forbidden)));
        var fixture = await CreateDurableFixtureAsync(graphProvider, resolver);
        try
        {
            var jobId = await SubmitCompactJobAsync(fixture, $"/rest/services/{serviceId}/ImageServer/exportTiles");

            // The numeric route deliberately resolves the primary publication sharing storage layer 0,
            // which is the restricted competitor. A service-scoped lifecycle route must not repeat
            // that ambiguous lookup after it already resolved target-publication.
            using var numericStatus = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/{jobId}");
            numericStatus.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // Authorization to another publication on the same storage layer must not adopt the
            // submitted publication's job for any lifecycle operation.
            using var otherStatus = await fixture.Client.GetAsync(
                $"/rest/services/{otherServiceId}/ImageServer/jobs/{jobId}");
            (await otherStatus.Content.ReadAsStringAsync()).ToLowerInvariant().Should().Contain("not found");

            using var otherCancel = await fixture.Client.PostAsync(
                $"/rest/services/{otherServiceId}/ImageServer/jobs/{jobId}/cancel",
                content: null);
            (await otherCancel.Content.ReadAsStringAsync()).ToLowerInvariant().Should().Contain("not found");

            using var otherResult = await fixture.Client.GetAsync(
                $"/rest/services/{otherServiceId}/ImageServer/jobs/{jobId}/results/out_service_url");
            (await otherResult.Content.ReadAsStringAsync()).ToLowerInvariant().Should().Contain("not found");

            using var status = await fixture.Client.GetAsync($"/rest/services/{serviceId}/ImageServer/jobs/{jobId}");
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            var statusBody = await status.Content.ReadAsStringAsync();
            statusBody.Should().Contain(jobId);
            statusBody.Should().Contain("esriJobSubmitted");

            // Rebinding the same publication id to a different storage layer must not let the
            // replacement resource adopt a job submitted against the original binding.
            targetStorageLayerId = 1;
            graphProvider.SetGraph(graph with
            {
                Revision = graph.Revision + 1,
                StorageBindings = graph.StorageBindings
                    .Select(static binding => binding.Metadata.Id == "target-binding"
                        ? binding with { StorageLayerId = 1 }
                        : binding)
                    .ToArray()
            });
            using var reboundStatus = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/jobs/{jobId}");
            (await reboundStatus.Content.ReadAsStringAsync()).ToLowerInvariant().Should().Contain("not found");

            targetStorageLayerId = TestLayerId;
            graphProvider.SetGraph(graph with { Revision = graph.Revision + 2 });

            using var cancel = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/jobs/{jobId}/cancel",
                content: null);
            cancel.StatusCode.Should().Be(HttpStatusCode.OK);
            (await cancel.Content.ReadAsStringAsync()).Should().Contain("esriJobCancelled");

            using var result = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/jobs/{jobId}/results/out_service_url");
            result.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

            await resolver.Received(5).ResolveFirstAccessibleLayerAsync(
                serviceId,
                Arg.Any<HttpContext>(),
                AuthorizationOperation.Export,
                Arg.Any<CancellationToken>());
            await resolver.Received(3).ResolveFirstAccessibleLayerAsync(
                otherServiceId,
                Arg.Any<HttpContext>(),
                AuthorizationOperation.Export,
                Arg.Any<CancellationToken>());
            await resolver.Received(1).ValidateLayerAsync(
                TestLayerId,
                Arg.Any<HttpContext>(),
                AuthorizationOperation.Export,
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/jobs/{jobId}")]
    public async Task JobStatus_UnknownJob_ReturnsSanitizedNotFound()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var status = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/te-does-not-exist");
            var body = await status.Content.ReadAsStringAsync();
            body.Should().NotContain("esriJobSubmitted");
            body.ToLowerInvariant().Should().Contain("not found");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<string> SubmitCompactJobAsync(WebAppFixture fixture, string exportTilesPath)
    {
        var submit = await fixture.Client.GetAsync(
            $"{exportTilesPath}?f=json&storageFormatType=esriMapCacheStorageModeCompactV2" +
            "&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0,1&format=png&maxTiles=1000");
        var body = await submit.Content.ReadAsStringAsync();
        var submitted = JsonSerializer.Deserialize(body, ImageServerJsonContext.Default.ImageServerExportTilesJobSubmitResponse);
        submitted.Should().NotBeNull($"submit response was: {body}");
        submitted!.JobStatus.Should().Be("esriJobSubmitted");
        return submitted.JobId;
    }

    private static async Task<WebAppFixture> CreateDurableFixtureAsync(
        TestMetadataV2GraphProvider? graphProvider = null,
        IImageServerLayerResolver? resolver = null)
    {
        // Submission and status only probe primary metadata + create/read the durable job record;
        // tile rendering happens later on the worker.
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RasterInfo
            {
                Id = 1,
                LayerId = TestLayerId,
                Name = "durable-export-source",
                Width = 256,
                Height = 256,
                BandCount = 3,
                PixelType = "8BUI",
                Srid = 4326,
                Extent = new RasterExtent
                {
                    XMin = -180,
                    YMin = -90,
                    XMax = 180,
                    YMax = 90,
                    Srid = 4326,
                },
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.AddSingleton(rasterStore);
            if (graphProvider is not null)
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                services.AddSingleton<IMetadataV2GraphStore>(graphProvider);
            }

            if (resolver is not null)
            {
                services.RemoveAll<IImageServerLayerResolver>();
                services.AddSingleton(resolver);
            }

            // The durable lifecycle service resolves its store/queue leniently; wire in-memory
            // doubles so a submission is durably created without a Redis dependency.
            services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore());
            services.AddSingleton<IJobQueue>(new InMemoryJobQueue());
        });
        await fixture.InitializeAsync();
        return fixture;
    }
}
